PortableDroid portable layout
=============================
This folder is the template for a PortableDroid installation. At release time
PortableDroid.exe is placed next to these folders and the whole tree is zipped.

  runtime/   QEMU + Android system image (NOT shipped in git - provisioned locally)
  profiles/  per-environment config + userdata (this is your Android data)
  apps/      imported APK files
  config/    settings.json
  cache/ logs/ backups/ temp/

All paths inside settings.json are RELATIVE to this folder. You may copy the whole
folder to any drive (D:\, E:\, USB) and it will keep working.

Never delete profiles/ - that is where your installed apps and their data live.
