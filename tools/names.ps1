param([string[]]$Va)
# Names the methods at the given addresses (hex VAs), from the [Address(RVA=...)] attributes of MelonLoader's
# Cpp2IL dummy assemblies. Builds a cached map once (scratchpad\methodmap.tsv).
$game = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path   # the game folder: ModMenuSource sits inside it
$map = Join-Path $PSScriptRoot "methodmap.tsv"
if (-not (Test-Path $map)) {
    Add-Type -Path "$game\MelonLoader\net6\Mono.Cecil.dll" -ErrorAction SilentlyContinue
    $sb = New-Object System.Text.StringBuilder
    foreach ($f in Get-ChildItem "$game\MelonLoader\Dependencies\Il2CppAssemblyGenerator\Cpp2IL\cpp2il_out\*.dll") {
        try { $asm = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($f.FullName) } catch { continue }
        foreach ($t in $asm.MainModule.GetTypes()) {
            foreach ($m in $t.Methods) {
                $a = $m.CustomAttributes | Where-Object { $_.AttributeType.Name -eq "AddressAttribute" } | Select-Object -First 1
                if (-not $a) { continue }
                $rva = ($a.Fields | Where-Object { $_.Name -eq "RVA" }).Argument.Value
                $len = ($a.Fields | Where-Object { $_.Name -eq "Length" }).Argument.Value
                if (-not $rva) { continue }
                [void]$sb.AppendLine("$([Convert]::ToUInt64($rva.Substring(2),16))`t$([Convert]::ToUInt64($len.Substring(2),16))`t$($t.FullName)::$($m.Name)")
            }
        }
    }
    [IO.File]::WriteAllText($map, $sb.ToString())
}
$rows = Get-Content $map | ForEach-Object { $p = $_.Split("`t"); [pscustomobject]@{ Rva = [UInt64]$p[0]; Len = [UInt64]$p[1]; Name = $p[2] } }
foreach ($v in $Va) {
    $rva = [Convert]::ToUInt64($v, 16) - 0x180000000
    $hit = $rows | Where-Object { $_.Rva -le $rva -and $rva -lt $_.Rva + [Math]::Max(1, $_.Len) } | Select-Object -First 3
    if ($hit) { foreach ($h in $hit) { "{0}  {1}{2}" -f $v, $h.Name, $(if ($h.Rva -ne $rva) { " +0x{0:X}" -f ($rva - $h.Rva) } else { "" }) } } else { "$v  ?" }
}
