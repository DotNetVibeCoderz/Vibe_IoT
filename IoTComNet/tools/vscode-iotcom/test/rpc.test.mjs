// End-to-end tests of the extension's RPC client against the real iotcom CLI (no VS Code needed).
// Run: npm test   (set IOTCOM_CLI_DLL to use a specific build of iotcom.dll)
import { test, after } from "node:test";
import assert from "node:assert/strict";
import { existsSync, readFileSync, rmSync, statSync } from "node:fs";
import { tmpdir } from "node:os";
import { join, dirname } from "node:path";
import { fileURLToPath } from "node:url";
import { createRequire } from "node:module";

const require = createRequire(import.meta.url);
const { IotcomRpc, RpcError } = require("../out/rpc.js");
const { resolveCli, splitCommandLine } = require("../out/cli.js");

const here = dirname(fileURLToPath(import.meta.url));
const dll = process.env.IOTCOM_CLI_DLL
  ?? ["Release", "Debug"].map((c) => join(here, "..", "..", "iotcom-cli", "bin", c, "net10.0", "iotcom.dll")).filter(existsSync)
    .sort((a, b) => statSync(b).mtimeMs - statSync(a).mtimeMs)[0];   // newest build: a stale one may lack `rpc`
assert.ok(dll, "build the CLI first: dotnet build tools/iotcom-cli");
const rpc = new IotcomRpc("dotnet", [dll]);
after(() => rpc.dispose());

test("initialize lists protocols, decoders and monitor sources", async () => {
  const info = await rpc.initialize();
  assert.match(info.version, /^\d+\.\d+\.\d+/);
  assert.match(info.credit.en, /Gravicode Studios/);
  const ids = info.protocols.map((p) => p.id);
  for (const id of ["modbus", "can", "uds", "coap", "mavlink"]) assert.ok(ids.includes(id), id);
  assert.ok(info.protocols.find((p) => p.id === "modbus").decoders.includes("modbus-tcp"));
  assert.ok(info.monitorSources.includes("sim:can"));
  assert.match(info.docsBase, /DotNetVibeCoderz\/Vibe_IoT/);
});

test("decode returns frame-lane fields", async () => {
  const modbus = await rpc.decode("modbus-tcp", "00 01 00 00 00 06 01 03 00 00 00 0A");
  assert.equal(modbus.summary, "ReadHoldingRegisters");
  assert.deepEqual(modbus.fields.map((f) => f.name).slice(0, 5), ["Transaction", "Protocol", "Length", "Unit", "Function"]);
  assert.equal(modbus.fields.find((f) => f.name === "Function").kind, "function");

  const coap = await rpc.decode("coap", "40017D34BB74656D7065726174757265");
  assert.match(coap.summary, /CON GET \/temperature/);

  const can = await rpc.decode("can", Buffer.from("7E8#04410C1AF8").toString("hex"));
  assert.equal(can.summary, "7E8#04410C1AF8");

  const mav = await rpc.decode("mavlink", "FE09000101000000000002035104037DDD");
  assert.match(mav.summary, /HEARTBEAT/);
});

test("errors come back as JSON-RPC errors", async () => {
  await assert.rejects(rpc.decode("nope", "00"), (e) => e instanceof RpcError && e.code === -32602);
  await assert.rejects(rpc.request("no.such.method"), (e) => e instanceof RpcError && e.code === -32601);
});

test("a monitor streams decoded frames and saves pcapng", async () => {
  const frames = [];
  const off = rpc.onFrame((f) => frames.push(f));
  const { id } = await rpc.startMonitor("sim:coap");
  await new Promise((r) => setTimeout(r, 2500));
  const path = join(tmpdir(), `iotcom-vscode-${process.pid}.pcapng`);
  const saved = await rpc.saveMonitor(id, path);
  const stopped = await rpc.stopMonitor(id);
  off();
  assert.ok(frames.length > 0, "frames were streamed");
  assert.ok(frames.every((f) => f.monitor === id && f.protocol === "coap"));
  assert.ok(frames.some((f) => f.fields.some((x) => x.name === "Code")));
  assert.ok(saved.frames > 0 && stopped.frames >= saved.frames);
  const head = readFileSync(path);
  assert.equal(head.readUInt32LE(0), 0x0a0d0d0a, "pcapng section header");
  rmSync(path);
});

test("the CLI is found from settings, a workspace build or PATH", () => {
  assert.deepEqual(splitCommandLine('dotnet "C:\\My Tools\\iotcom.dll"'), ["dotnet", "C:\\My Tools\\iotcom.dll"]);
  assert.equal(resolveCli("iotcom", []).command, "iotcom");
  const repo = join(here, "..", "..", "..");
  assert.equal(resolveCli("", [repo]).command, "dotnet");
  assert.equal(resolveCli(undefined, [tmpdir()]).description, "iotcom (global tool)");
});
