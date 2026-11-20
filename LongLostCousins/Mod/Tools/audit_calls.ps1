param(
    [string]$Dll = "",
    [switch]$SelfTest,
    [switch]$Quiet
)
# What does this check?
# Every game and UnityEngine member the built mod calls must have compiled code in this game's build. The interop
# assemblies (MelonLoader/Il2CppAssemblies) list members the game's binary may not have: MelonLoader rebuilds stripped
# ones in managed code, and calling those is never safe. This script reads the built DLL with Mono.Cecil, lists every
# member it references in the interop assemblies, and looks each one up in Cpp2IL's dummy assemblies, where a member
# with code carries an [Address(RVA=...)] attribute.
#
# What does it print?
#   OK       a method with code (its RVA)
#   FIELD    a field read or written in place (an interop property over a game field, or a struct field): no code needed
#   NO BODY  a method without code in this build: never call it
#   NOT FOUND a member the dummy assemblies do not have
# Then each Harmony hook target the mod installs through HookSet.Prefix/Postfix or the mod's HookPlus.Hook (prefix,
# postfix and finalizer in one call), with its RVA and how many methods of the whole game share that RVA (a shared body
# would fire the hook for the other methods too).
# The exit code is 1 when anything is NO BODY or NOT FOUND, or a hook target is missing, bodiless or shared.
#
# -SelfTest also checks known members: one with code must come out OK, stripped ones must come out NO BODY.

$ErrorActionPreference = "Stop"
$game = (Resolve-Path (Join-Path $PSScriptRoot "..\..\..\..")).Path   # Mod\Tools -> the game folder
$ml = Join-Path $game "MelonLoader"
$interopDir = Join-Path $ml "Il2CppAssemblies"
$dummyDir = Join-Path $ml "Dependencies\Il2CppAssemblyGenerator\Cpp2IL\cpp2il_out"
if ($Dll -eq "") { $Dll = Join-Path $PSScriptRoot "..\bin\Release\net6.0\LongLostCousins.dll" }
Add-Type -Path (Join-Path $ml "net6\Mono.Cecil.dll")

$interopNames = @{}
foreach ($f in Get-ChildItem (Join-Path $interopDir "*.dll")) { $interopNames[[IO.Path]::GetFileNameWithoutExtension($f.Name)] = $true }

# ---------------------------------------------------------------- dummy assemblies, loaded on demand
$dummies = @{}
function Get-Dummy([string]$interopAsm) {
    $name = $interopAsm
    if (-not (Test-Path (Join-Path $dummyDir "$name.dll")) -and $name.StartsWith("Il2Cpp")) { $name = $name.Substring(6) }
    if ($dummies.ContainsKey($name)) { return $dummies[$name] }
    $path = Join-Path $dummyDir "$name.dll"
    $types = $null
    if (Test-Path $path) {
        $types = [System.Collections.Hashtable]::new([StringComparer]::Ordinal)   # type names are case-sensitive
        $asm = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($path)
        foreach ($t in $asm.MainModule.GetTypes()) { $types[$t.FullName] = $t }
    }
    $dummies[$name] = $types
    return $types
}

function Get-DummyTypeName([Mono.Cecil.TypeReference]$t) {
    if ($t.IsNested) {
        $n = $t.Name
        if ($n -match '^_(.+)_d__(\d+)$') { $n = "<$($Matches[1])>d__$($Matches[2])" }
        return (Get-DummyTypeName $t.DeclaringType) + "/" + $n
    }
    $ns = $t.Namespace
    if ($ns -eq "Il2Cpp") { $ns = "" }
    elseif ($ns.StartsWith("Il2Cpp")) { $ns = $ns.Substring(6) }
    if ($ns -eq "") { return $t.Name }
    return "$ns.$($t.Name)"
}

function Get-Short([Mono.Cecil.TypeReference]$t) {
    if ($t -is [Mono.Cecil.ByReferenceType]) { return (Get-Short $t.ElementType) + "&" }
    if ($t -is [Mono.Cecil.ArrayType]) { return (Get-Short $t.ElementType) + "[]" }
    if ($t -is [Mono.Cecil.GenericInstanceType]) {
        $en = $t.ElementType.Name
        if ($en -eq 'Il2CppStructArray`1' -or $en -eq 'Il2CppReferenceArray`1' -or $en -eq 'Il2CppArrayBase`1' -or $en -eq 'Il2CppStringArray') {
            return (Get-Short $t.GenericArguments[0]) + "[]"
        }
        return $en
    }
    if ($t.Name -eq 'Il2CppStringArray') { return "String[]" }
    return $t.Name
}

function Get-Scope([Mono.Cecil.TypeReference]$t) {
    while ($t.IsNested) { $t = $t.DeclaringType }
    if ($t -is [Mono.Cecil.TypeSpecification]) { $t = $t.ElementType; while ($t.IsNested) { $t = $t.DeclaringType } }
    $s = $t.Scope
    if ($s -is [Mono.Cecil.AssemblyNameReference]) { return $s.Name }
    if ($s -is [Mono.Cecil.ModuleDefinition]) { return $s.Assembly.Name.Name }
    return "$s"
}

function Get-Rva($m) {
    $a = $m.CustomAttributes | Where-Object { $_.AttributeType.Name -eq 'AddressAttribute' } | Select-Object -First 1
    if (-not $a) { return $null }
    $f = $a.Fields | Where-Object { $_.Name -eq 'RVA' } | Select-Object -First 1
    if ($f) { return [string]$f.Argument.Value }
    return $null
}

# Finds the dummy method (or field) for an interop method reference: status, detail and the dummy method itself.
function Resolve-Method([Mono.Cecil.MethodReference]$mr) {
    $dt = $mr.DeclaringType
    if ($dt -is [Mono.Cecil.GenericInstanceType]) { $dt = $dt.ElementType }
    $scope = Get-Scope $dt
    $types = Get-Dummy $scope
    $tn = Get-DummyTypeName $dt
    $label = "$tn::$($mr.Name)($((@($mr.Parameters) | ForEach-Object { Get-Short $_.ParameterType }) -join ', '))"
    if ($null -eq $types) { return @{ Status = "NO BODY"; Label = $label; Detail = "no Cpp2IL twin of $scope (the build has none of it)" } }
    $t = $types[$tn]
    if (-not $t) { return @{ Status = "NOT FOUND"; Label = $label; Detail = "type $tn not in $scope" } }
    $want = @($mr.Parameters | ForEach-Object { Get-Short $_.ParameterType })
    $gen = if ($mr.HasGenericParameters) { $mr.GenericParameters.Count } else { 0 }
    $cands = @($t.Methods | Where-Object { $_.Name -ceq $mr.Name -and $_.Parameters.Count -eq $want.Count -and $_.GenericParameters.Count -eq $gen })
    if ($cands.Count -gt 1) {
        $exact = @($cands | Where-Object { (@($_.Parameters | ForEach-Object { Get-Short $_.ParameterType }) -join ',') -eq ($want -join ',') })
        if ($exact.Count -ge 1) { $cands = $exact }
    }
    if ($cands.Count -eq 0) {
        if ($mr.Name -match '^[gs]et_(.+)$') {
            $fn = $Matches[1]
            $fd = $t.Fields | Where-Object { $_.Name -ceq $fn -or $_.Name -ceq "<$fn>k__BackingField" } | Select-Object -First 1
            if ($fd) { return @{ Status = "FIELD"; Label = $label; Detail = "field $($fd.Name)$(if ($fd.IsStatic) { ' (static)' })" } }
        }
        return @{ Status = "NOT FOUND"; Label = $label; Detail = "no method $($mr.Name) with $($want.Count) parameters in $tn" }
    }
    if ($cands.Count -gt 1) {
        $noBody = @($cands | Where-Object { -not (Get-Rva $_) })
        if ($noBody.Count -gt 0) { return @{ Status = "NO BODY"; Label = $label; Detail = "ambiguous overloads, $($noBody.Count) of $($cands.Count) without code" } }
        return @{ Status = "OK"; Label = $label; Detail = "ambiguous overloads, all with code"; Method = $cands[0] }
    }
    $rva = Get-Rva $cands[0]
    if ($rva) { return @{ Status = "OK"; Label = $label; Detail = $rva; Method = $cands[0] } }
    return @{ Status = "NO BODY"; Label = $label; Detail = "no compiled code in this build" }
}

function Test-Interop([Mono.Cecil.TypeReference]$t) { return $interopNames.ContainsKey((Get-Scope $t)) }

$bad = 0

# ---------------------------------------------------------------- self-test
if ($SelfTest) {
    $core = [Mono.Cecil.AssemblyDefinition]::ReadAssembly((Join-Path $interopDir "UnityEngine.CoreModule.dll")).MainModule
    $img = [Mono.Cecil.AssemblyDefinition]::ReadAssembly((Join-Path $interopDir "UnityEngine.ImageConversionModule.dll")).MainModule
    $cases = @(
        @{ Type = $core.GetType("UnityEngine.Renderer"); Name = "set_enabled"; Want = "OK" },
        @{ Type = $core.GetType("UnityEngine.Texture2D"); Name = "LoadRawTextureData"; Params = 1; Want = "OK" },
        @{ Type = $img.GetType("UnityEngine.ImageConversion"); Name = "LoadImage"; Params = 2; Want = "NO BODY" },
        @{ Type = $core.GetType("UnityEngine.Renderer"); Name = "get_shadowCastingMode"; Want = "NOT OK" },
        @{ Type = $core.GetType("UnityEngine.Renderer"); Name = "get_receiveShadows"; Want = "NOT OK" }
    )
    foreach ($c in $cases) {
        $m = $c.Type.Methods | Where-Object { $_.Name -eq $c.Name -and ($null -eq $c.Params -or $_.Parameters.Count -eq $c.Params) } | Select-Object -First 1
        if (-not $m) { "SELFTEST {0}::{1} is not in the interop assembly{2}" -f $c.Type.Name, $c.Name, $(if ($c.Want -eq "NOT OK") { " (stripped from the metadata too): counts as caught" } else { "" }); if ($c.Want -ne "NOT OK") { $bad++ }; continue }
        $r = Resolve-Method $m
        $pass = if ($c.Want -eq "NOT OK") { $r.Status -ne "OK" -and $r.Status -ne "FIELD" } else { $r.Status -eq $c.Want }
        "SELFTEST {0} {1}: {2} ({3}), want {4}" -f $(if ($pass) { "pass" } else { "FAIL" }), $r.Label, $r.Status, $r.Detail, $c.Want
        if (-not $pass) { $bad++ }
    }
}

# ---------------------------------------------------------------- the mod's references
$mod = [Mono.Cecil.AssemblyDefinition]::ReadAssembly((Resolve-Path $Dll).Path).MainModule
$rows = [System.Collections.Hashtable]::new([StringComparer]::Ordinal)   # get_instance and get_Instance are two members
foreach ($r in $mod.GetMemberReferences()) {
    $dt = $r.DeclaringType
    if (-not (Test-Interop $dt)) { continue }
    if ($r -is [Mono.Cecil.MethodReference]) {
        # Il2CppInterop's own wrapper constructor (IntPtr) is managed code, not a game method.
        if ($r.Name -eq ".ctor" -and $r.Parameters.Count -eq 1 -and $r.Parameters[0].ParameterType.FullName -eq "System.IntPtr") { continue }
        $res = Resolve-Method $r
    }
    else {
        $res = @{ Status = "FIELD"; Label = "$(Get-DummyTypeName $dt)::$($r.Name)"; Detail = "struct field, read or written in place" }
    }
    $rows[$res.Label] = $res
}
$counts = @{}
foreach ($k in ($rows.Keys | Sort-Object)) {
    $res = $rows[$k]
    $counts[$res.Status] = 1 + $(if ($counts.ContainsKey($res.Status)) { $counts[$res.Status] } else { 0 })
    if ($res.Status -eq "NO BODY" -or $res.Status -eq "NOT FOUND") { $bad++ }
    if (-not $Quiet -or $res.Status -eq "NO BODY" -or $res.Status -eq "NOT FOUND") { "{0,-9} {1}  [{2}]" -f $res.Status, $res.Label, $res.Detail }
}
"members: " + (($counts.Keys | Sort-Object | ForEach-Object { "$_ $($counts[$_])" }) -join ", ")

# ---------------------------------------------------------------- hook targets
# HookSet.Prefix/Postfix(typeof(T), nameof(T.M), typeof(H), nameof(H.N), [Type[] args]) and
# HookPlus.Hook(set, typeof(T), nameof(T.M), typeof(H), prefix, postfix, finalizer, [Type[] args]): the first (ldtoken,
# ldstr) pair after the previous hook call is the target; Type.EmptyTypes or a new Type[] { typeof(...) } narrows the overload.
function Test-HookCall($op) {
    $dt = $op.DeclaringType.Name
    return ($dt -eq "HookSet" -and ($op.Name -eq "Prefix" -or $op.Name -eq "Postfix")) -or ($dt -eq "HookPlus" -and $op.Name -eq "Hook")
}
$hooks = @()
foreach ($t in $mod.GetTypes()) {
    foreach ($m in $t.Methods) {
        if (-not $m.HasBody) { continue }
        $pairs = @(); $argTypes = $null; $sawArgs = $false
        $ins = @($m.Body.Instructions)
        for ($i = 0; $i -lt $ins.Count; $i++) {
            $x = $ins[$i]
            if ($x.OpCode.Code -eq [Mono.Cecil.Cil.Code]::Ldtoken -and $i + 2 -lt $ins.Count -and $ins[$i + 2].OpCode.Code -eq [Mono.Cecil.Cil.Code]::Ldstr) {
                $pairs += , @($x.Operand, [string]$ins[$i + 2].Operand)
            }
            if ($x.OpCode.Code -eq [Mono.Cecil.Cil.Code]::Ldsfld -and $x.Operand.Name -eq "EmptyTypes" -and $pairs.Count -ge 2) { $argTypes = @(); $sawArgs = $true }
            if ($x.OpCode.Code -eq [Mono.Cecil.Cil.Code]::Newarr -and $x.Operand.FullName -eq "System.Type" -and $pairs.Count -ge 2) {
                $argTypes = @(); $sawArgs = $true
                for ($j = $i + 1; $j -lt $ins.Count; $j++) {
                    $y = $ins[$j]
                    if ($y.OpCode.Code -eq [Mono.Cecil.Cil.Code]::Ldtoken) { $argTypes += (Get-Short $y.Operand) }
                    if ($y.OpCode.Code -eq [Mono.Cecil.Cil.Code]::Call -or $y.OpCode.Code -eq [Mono.Cecil.Cil.Code]::Callvirt) {
                        if ($y.Operand.DeclaringType.Name -eq "HookSet" -or $y.Operand.DeclaringType.Name -eq "HookPlus") { break }
                    }
                }
            }
            if (($x.OpCode.Code -eq [Mono.Cecil.Cil.Code]::Call -or $x.OpCode.Code -eq [Mono.Cecil.Cil.Code]::Callvirt) -and (Test-HookCall $x.Operand)) {
                # Narrow says whether an argument list was given (Type.EmptyTypes is an empty one, which PowerShell would turn into $null).
                if ($pairs.Count -ge 1) { $hooks += , @{ Type = $pairs[0][0]; Method = $pairs[0][1]; Narrow = $sawArgs; Args = @($argTypes); Kind = $x.Operand.Name; In = "$($t.Name).$($m.Name)" } }
                $pairs = @(); $argTypes = $null; $sawArgs = $false
            }
        }
    }
}

if ($hooks.Count -gt 0) {
    # Every method RVA of the whole game, counted, to find bodies shared by several methods.
    $rvaCount = @{}
    foreach ($f in Get-ChildItem (Join-Path $dummyDir "*.dll")) {
        try { $asm = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($f.FullName) } catch { continue }
        foreach ($t in $asm.MainModule.GetTypes()) {
            foreach ($m in $t.Methods) {
                $rva = Get-Rva $m
                if ($rva) { $rvaCount[$rva] = 1 + $(if ($rvaCount.ContainsKey($rva)) { $rvaCount[$rva] } else { 0 }) }
            }
        }
    }
    foreach ($h in $hooks) {
        $types = Get-Dummy (Get-Scope $h.Type)
        $tn = Get-DummyTypeName $h.Type
        $t = if ($types) { $types[$tn] } else { $null }
        $cands = if ($t) { @($t.Methods | Where-Object { $_.Name -ceq $h.Method }) } else { @() }
        if ($h.Narrow) { $cands = @($cands | Where-Object { (@($_.Parameters | ForEach-Object { Get-Short $_.ParameterType }) -join ',') -eq (@($h.Args) -join ',') }) }
        if ($cands.Count -eq 0) { "HOOK BAD  {0} {1}::{2}({3}): not found  [from {4}]" -f $h.Kind, $tn, $h.Method, ($h.Args -join ', '), $h.In; $bad++; continue }
        foreach ($c in $cands) {
            $rva = Get-Rva $c
            $ps = (@($c.Parameters | ForEach-Object { Get-Short $_.ParameterType }) -join ', ')
            if (-not $rva) { "HOOK BAD  {0} {1}::{2}({3}): no compiled code  [from {4}]" -f $h.Kind, $tn, $h.Method, $ps, $h.In; $bad++; continue }
            $va = "0x{0:X}" -f (0x180000000 + [Convert]::ToUInt64($rva.Substring(2), 16))
            $n = $rvaCount[$rva]
            $ok = $n -eq 1
            "HOOK {0}  {1} {2}::{3}({4}) at VA {5}, {6} method(s) at that RVA  [from {7}]" -f $(if ($ok) { "OK  " } else { "BAD " }), $h.Kind, $tn, $h.Method, $ps, $va, $n, $h.In
            if (-not $ok) { $bad++ }
        }
    }
}
else { "hooks: none found in the DLL" }

if ($bad -gt 0) { "AUDIT FAIL: $bad problem(s)"; exit 1 }
"AUDIT PASS"
exit 0
