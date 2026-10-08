# ipc_test.ps1 - end-to-end test of kazumi mpv IPC
$pipe = new-object System.IO.Pipes.NamedPipeClientStream(".", "kazumi-mpv-ipc", [System.IO.Pipes.PipeDirection]::InOut)
$pipe.Connect(3000)
$writer = New-Object System.IO.StreamWriter($pipe, (New-Object System.Text.UTF8Encoding($false)))
$writer.AutoFlush = $true
$reader = New-Object System.IO.StreamReader($pipe, [System.Text.Encoding]::UTF8)

function Send-Recv($json) {
    $writer.WriteLine($json)
    $line = $reader.ReadLine()
    return $line
}

$r1 = Send-Recv '{"command":["get_property_string","mpv-version"],"request_id":101}'
Write-Output ("mpv-version   : " + $r1)
$r2 = Send-Recv '{"command":["get_property_string","path"],"request_id":102}'
Write-Output ("path          : " + $r2)
$r3 = Send-Recv '{"command":["get_property","duration"],"request_id":103}'
Write-Output ("duration      : " + $r3)

# frame-step x2 (fire and read ack)
$r4 = Send-Recv '{"command":["frame-step"],"request_id":104}'
Write-Output ("frame-step    : " + $r4)
$r5 = Send-Recv '{"command":["frame-back-step"],"request_id":105}'
Write-Output ("frame-back    : " + $r5)

# screenshot to temp file
$tmp = "$env:TEMP\kazumi_ipc_test.png"
$tmpMpv = $tmp.Replace('\','/')
$r6 = Send-Recv ('{"command":["screenshot-to-file","' + $tmpMpv + '"],"request_id":106}')
Write-Output ("screenshot    : " + $r6)
Start-Sleep 1
if (Test-Path $tmp) {
    Write-Output ("screenshot file OK: {0} bytes" -f (Get-Item $tmp).Length)
    Remove-Item $tmp -Force
} else {
    Write-Output "screenshot file MISSING"
}

$pipe.Dispose()
