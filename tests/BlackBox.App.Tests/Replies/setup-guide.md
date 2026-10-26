Here's how to set up the test VM on your laptop. It takes about 15 minutes, most of it downloads.

## Before you start

You need **Windows 10 Pro or Enterprise** (Hyper-V isn't in Home) and admin rights. Check that virtualization is on:

```powershell
(Get-CimInstance Win32_Processor).VirtualizationFirmwareEnabled
```

If it says `False`, turn it on in the BIOS first.

## Steps

1. **Turn on Hyper-V.** Open an admin PowerShell and run:

   ```powershell
   Enable-WindowsOptionalFeature -Online -FeatureName Microsoft-Hyper-V -All
   ```

   Windows asks to restart. Say yes; it reboots twice while it installs the feature, which is normal.

2. **Install Multipass** from https://multipass.run/install. Pick the Hyper-V backend when the installer asks, not VirtualBox.
3. **Create the VM** with the cloud-init file from the repo:
   - `--cpus 4` and `--memory 8G` are enough for the whole stack;
   - `--disk 30G` leaves room for the database and Docker images, which grow quickly once the stability tests start writing their results every few seconds over a long night run.
4. **Check it works:** `multipass list` should show `hoc-test` as *Running* with an IPv4 address.

> **Note:** the VM's IP can change after a reboot of the host. The script reads it fresh each time, so you don't need to do anything about it.

> [!WARNING]
> Don't run `multipass purge` while a test run is going: it deletes the VM's disk.

### What you get

| Part | Where | Port |
|---|---|--:|
| API server | `hoc-test` VM | 5105 |
| Postgres | same VM, in Docker | 5432 |
| nginx | same VM | 80 |

That's it. Next, I'd write `test-stack.ps1` so one command does all of the above. Want me to?
