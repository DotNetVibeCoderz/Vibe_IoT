#!/usr/bin/env python3
"""Runs every notebook headlessly (CI gate): extracts the code cells of each *.en.ipynb, hoists `using`
directives, compiles them as a console program against the local projects and runs it.
EN and ID notebooks share identical code (verified here), so running EN covers both.

Run: python build/check_notebooks.py [work-dir]
"""
import glob
import json
import os
import re
import subprocess
import sys
import tempfile

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), ".."))
work = sys.argv[1] if len(sys.argv) > 1 else tempfile.mkdtemp(prefix="iotcom-nb-")
failures = 0


def code_cells(path):
    return ["".join(c["source"]) for c in json.load(open(path, encoding="utf-8"))["cells"] if c["cell_type"] == "code"]


for nb in sorted(glob.glob(os.path.join(ROOT, "notebooks", "**", "*.en.ipynb"), recursive=True)):
    twin = nb.replace(".en.ipynb", ".id.ipynb")
    rel = os.path.relpath(nb, ROOT)
    if not os.path.exists(twin) or code_cells(twin) != code_cells(nb):
        print(f"FAIL {rel}: Indonesian twin missing or code differs")
        failures += 1
        continue
    usings, body = set(), []
    for cell in code_cells(nb):
        for line in cell.split("\n"):
            if line.startswith(("#r ", "#i ")):
                continue
            (usings.add(line) if re.match(r"^using [A-Za-z0-9_.]+;$", line) else body.append(line))
    if not any(l.strip() for l in body):
        print(f"skip {rel} (no code)")
        continue
    name = re.sub(r"[^A-Za-z0-9]", "_", os.path.basename(nb)[:-len(".en.ipynb")])
    d = os.path.join(work, name)
    os.makedirs(d, exist_ok=True)
    with open(os.path.join(d, "Program.cs"), "w", encoding="utf-8") as f:
        f.write("\n".join(sorted(usings)) + "\n\n" + "\n".join(body) + "\n")
    root = ROOT.replace("\\", "/")
    with open(os.path.join(d, f"{name}.csproj"), "w", encoding="utf-8") as f:
        f.write(f"""<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><Nullable>enable</Nullable>
  <ImplicitUsings>enable</ImplicitUsings><ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally><NoWarn>CS1998;CS8602</NoWarn></PropertyGroup>
  <ItemGroup><ProjectReference Include="{root}/src/IoTCom.Net/IoTCom.Net.csproj" />
  <ProjectReference Include="{root}/src/IoTCom.Net.Native.Modbus/IoTCom.Net.Native.Modbus.csproj" />
  <ProjectReference Include="{root}/src/IoTCom.Net.Adapters.Dicom/IoTCom.Net.Adapters.Dicom.csproj" />
  <ProjectReference Include="{root}/src/IoTCom.Net.Protocols.Uds/IoTCom.Net.Protocols.Uds.csproj" />
  <ProjectReference Include="{root}/src/IoTCom.Net.Serialization.Protobuf/IoTCom.Net.Serialization.Protobuf.csproj" />
  <ProjectReference Include="{root}/src/IoTCom.Net.Serialization.MessagePack/IoTCom.Net.Serialization.MessagePack.csproj" />
  <ProjectReference Include="{root}/src/IoTCom.Net.Adapters.OpcUa/IoTCom.Net.Adapters.OpcUa.csproj" />
  <ProjectReference Include="{root}/src/IoTCom.Net.Adapters.Zenoh/IoTCom.Net.Adapters.Zenoh.csproj" />
  <ProjectReference Include="{root}/src/IoTCom.Net.Adapters.Amqp/IoTCom.Net.Adapters.Amqp.csproj" /></ItemGroup>
</Project>""")
    env = dict(os.environ)
    native = os.path.join(ROOT, "rust", "target", "release")
    if "IOTCOM_NATIVE_PATH" not in env and os.path.isdir(native):
        env["IOTCOM_NATIVE_PATH"] = native  # notebooks build outside the repo, so point them at the Rust libraries
    run = subprocess.run(["dotnet", "run", "-v", "q"], cwd=d, capture_output=True, text=True, timeout=600, env=env)
    if run.returncode != 0:
        print(f"FAIL {rel}\n{run.stdout[-2000:]}\n{run.stderr[-2000:]}")
        failures += 1
    else:
        print(f"ok   {rel}")

sys.exit(1 if failures else 0)
