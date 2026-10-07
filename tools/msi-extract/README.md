# MSI extraction tools

Pure-Python (plus `olefile`) helpers used to take apart the original Banner 3.1
installer without needing Windows, `msitools`, or `cabextract`.

```sh
pip install olefile
mkdir -p out/streams out/files
python3 -I msidump.py   Banner3.1.msi out/streams        # dump OLE streams (tables, binaries, Data1.cab)
python3 -I msitables.py out/streams File Registry Feature # print MSI tables (omit names to print all)
python3 -I cabx.py      out/streams/Data1.cab out/files  # extract MSZIP cabinet contents
```

Keep extracted vendor files out of the repo (see `.gitignore`).
