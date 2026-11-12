param([string]$Type, [string]$Method = "")
# Method addresses from MelonLoader's Cpp2IL dummy assemblies ([Address(RVA=..., VA=...)] attributes), read with Mono.Cecil.
$game = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path   # the game folder: ModMenuSource sits inside it
Add-Type -Path "$game\MelonLoader\net6\Mono.Cecil.dll" -ErrorAction SilentlyContinue
$asm = [Mono.Cecil.AssemblyDefinition]::ReadAssembly("$game\MelonLoader\Dependencies\Il2CppAssemblyGenerator\Cpp2IL\cpp2il_out\Assembly-CSharp.dll")
$types = $asm.MainModule.GetTypes() | Where-Object { $_.FullName -eq $Type -or $_.Name -eq $Type -or $_.FullName -like "*$Type" }
foreach ($t in $types) {
    foreach ($m in $t.Methods) {
        if ($Method -ne "" -and $m.Name -notlike $Method) { continue }
        $a = $m.CustomAttributes | Where-Object { $_.AttributeType.Name -like "Address*" } | Select-Object -First 1
        $va = if ($a) { ($a.Fields | Where-Object { $_.Name -eq "VA" }).Argument.Value } else { "?" }
        "{0}.{1}({2})  VA {3}" -f $t.Name, $m.Name, (($m.Parameters | ForEach-Object { $_.ParameterType.Name }) -join ","), $va
    }
}
