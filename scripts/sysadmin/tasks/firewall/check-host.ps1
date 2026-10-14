# Port checks from this PC, which the VM sees as 10.0.2.2, the monitoring host; prints PASS/FAIL lines.
# Two throwaway listeners show that HTTPS is let through and that other ports are not.
Invoke-LabSsh "sudo systemd-run --quiet --unit=lab-probe-443 python3 -m http.server 443 --bind 0.0.0.0; sudo systemd-run --quiet --unit=lab-probe-8443 python3 -m http.server 8443 --bind 0.0.0.0; sleep 2" | Out-Null
foreach ($p in @(@(22, $true, "SSH"), @(80, $true, "HTTP"), @(443, $true, "HTTPS"), @(9100, $true, "metrics, from the monitoring host"), @(6379, $false, "the internal cache"), @(8443, $false, "anything else (a test listener on 8443)"))) {
  $open = Test-LabPort $p[0]
  $what = "port $($p[0]) ($($p[2])) is $(if ($p[1]) { 'reachable' } else { 'blocked' }) from outside"
  if ($open -eq $p[1]) { "PASS $what" } else { "FAIL $what" }
}
Invoke-LabSsh "sudo systemctl stop lab-probe-443 lab-probe-8443" | Out-Null
