## Other options
| Option | Verdict |
|---|---|
| **WSL2 + Docker Engine inside Ubuntu** | Lightest and fastest. Ports reach Windows on `localhost`, so Unity needs no IP. But it isn't really a separate server: it shares one VM with every other WSL distro and has no real snapshots. The better networking mode ("mirrored") needs Windows 11, so on your Windows 10 you get the older mode. Good if you just want containers with the least setup. |
| **Vagrant + VirtualBox/Hyper-V** | Works, but heavier: you write a Vagrantfile and install VirtualBox. Multipass does the same job more simply. |
| **Plain QEMU / Hyper-V Manager** | Too much manual work for a setup you want to recreate easily. |

## What Multipass does that you'd script yourself with QEMU
| Task | Multipass | QEMU on Windows |
|---|---|---|
| **Get an OS** | Downloads and caches Ubuntu images | Download an Ubuntu cloud image yourself, then `qemu-img create -b ... -F qcow2` an overlay and `qemu-img resize` it to the disk size you want |
| **First-boot setup** (user, SSH key, Docker) | `--cloud-init file.yaml` | Supply the cloud-init files yourself: build a small seed ISO (the usual tool, `cloud-localds`, doesn't exist on Windows), or serve them over HTTP and point the VM at it with `-smbios type=1,serial=ds=nocloud;s=http://10.0.2.2:8000/` |
| **Speed** | Uses Hyper-V automatically | Turn on the *Windows Hypervisor Platform* feature and pass `-accel whpx`. If you forget, QEMU silently falls back to pure software emulation (its default), which is very slow. HAXM (an older speed-up option) is Intel-only, so it's no use on your Ryzen. |
| **Networking** | The VM gets its own IP on Hyper-V's default network | QEMU's default networking isn't reachable from Windows, so every port needs a forward (`hostfwd=tcp::2222-:22,hostfwd=tcp::5105-:5105,...`). A real IP of its own needs the TAP-Windows driver and a network bridge |
| **Snapshots** | `multipass snapshot` / `restore` | `qemu-img snapshot` on the disk image (VM powered off), or `savevm`/`loadvm` in the QEMU monitor (QEMU's built-in console) |
