param(
    [string]$Va,              # hex virtual address, e.g. 1805EB700
    [int]$Count = 40,         # instructions
    [string]$Table = "",      # hex VA of a jump table of int32 RVAs (prints targets)
    [int]$Entries = 0
)
# Disassembles GameAssembly.dll with MelonLoader's own Iced.dll (no downloads).
$game = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path   # the game folder: ModMenuSource sits inside it
Add-Type -Path "$game\MelonLoader\net6\Iced.dll" -ErrorAction SilentlyContinue
if (-not $script:bytes) { $script:bytes = [IO.File]::ReadAllBytes("$game\GameAssembly.dll") }
$b = $script:bytes
$pe = [BitConverter]::ToInt32($b, 0x3c); $n = [BitConverter]::ToUInt16($b, $pe + 6); $osz = [BitConverter]::ToUInt16($b, $pe + 20)
$base = [BitConverter]::ToUInt64($b, $pe + 24 + 24); $sec = $pe + 24 + $osz
function Off([UInt64]$va) {
    $rva = $va - $base
    for ($i = 0; $i -lt $n; $i++) {
        $o = $sec + 40 * $i
        $vs = [BitConverter]::ToUInt32($b, $o + 8); $vaS = [BitConverter]::ToUInt32($b, $o + 12)
        $rs = [BitConverter]::ToUInt32($b, $o + 16); $ro = [BitConverter]::ToUInt32($b, $o + 20)
        if ($rva -ge $vaS -and $rva -lt $vaS + [Math]::Max($vs, $rs)) { return [int]($ro + $rva - $vaS) }
    }
    throw "address not mapped"
}
if ($Table -ne "") {
    $t = [Convert]::ToUInt64($Table, 16); $o = Off $t
    for ($i = 0; $i -lt $Entries; $i++) { "{0,2}: {1:X}" -f ($i + 1), ($base + [BitConverter]::ToUInt32($b, $o + 4 * $i)) }
    return
}
$addr = [Convert]::ToUInt64($Va, 16)
$o = Off $addr
$len = [Math]::Min(16 * $Count, $b.Length - $o)
$slice = New-Object byte[] $len; [Array]::Copy($b, $o, $slice, 0, $len)
$reader = New-Object Iced.Intel.ByteArrayCodeReader -ArgumentList (, $slice)
$dec = [Iced.Intel.Decoder]::Create(64, $reader); $dec.IP = $addr
$fmt = New-Object Iced.Intel.MasmFormatter
$out = New-Object Iced.Intel.StringOutput
for ($i = 0; $i -lt $Count; $i++) {
    $ins = $dec.Decode()
    $fmt.Format([ref]$ins, $out)
    "{0:X}  {1}" -f $ins.IP, $out.ToStringAndReset()
}
