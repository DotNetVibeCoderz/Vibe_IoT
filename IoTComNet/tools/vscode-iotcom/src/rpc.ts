// JSON-RPC 2.0 client for `iotcom rpc` (one JSON object per line over stdio).
// Deliberately free of the `vscode` module so it can be tested with plain Node (test/rpc.test.mjs).
import { ChildProcessWithoutNullStreams, spawn } from "child_process";
import { createInterface } from "readline";

export interface FrameField {
  name: string;
  offset: number;
  length: number;
  kind: "header" | "address" | "function" | "length" | "data" | "checksum" | "delimiter" | "error";
  value?: string | null;
}

export interface Decoded {
  hex: string;
  summary: string;
  fields: FrameField[];
}

export interface ProtocolInfo {
  id: string;
  name: string;
  package: string;
  roles: string;
  docs: string;
  notebook?: string | null;
  sample?: string | null;
  decoders: string[];
}

export interface InitializeResult {
  name: string;
  version: string;
  credit: { en: string; id: string };
  docsBase: string;
  protocols: ProtocolInfo[];
  monitorSources: string[];
}

export interface Devices {
  serialPorts: string[];
  canInterfaces: string[];
  simulators: string[];
}

export interface FrameEvent {
  monitor: string;
  time: string;
  protocol: string;
  direction: "in" | "out";
  endpoint?: string | null;
  hex: string;
  summary?: string | null;
  fields: FrameField[];
}

type Pending = { resolve: (v: unknown) => void; reject: (e: Error) => void; timer: NodeJS.Timeout };

export class RpcError extends Error {
  constructor(public readonly code: number, message: string) {
    super(message);
  }
}

/** One `iotcom rpc` process. Requests are matched by id; `frame` notifications go to `onFrame` listeners. */
export class IotcomRpc {
  private readonly proc: ChildProcessWithoutNullStreams;
  private readonly pending = new Map<number, Pending>();
  private readonly frameListeners = new Set<(f: FrameEvent) => void>();
  private nextId = 1;
  private exited: Error | undefined;

  constructor(command: string, args: string[], cwd?: string, private readonly log: (line: string) => void = () => {}) {
    this.proc = spawn(command, [...args, "rpc"], { cwd, stdio: ["pipe", "pipe", "pipe"], windowsHide: true, shell: false });
    createInterface({ input: this.proc.stdout }).on("line", (line) => this.onLine(line));
    createInterface({ input: this.proc.stderr }).on("line", (line) => this.log(line));
    const fail = (e: Error) => {
      this.exited = e;
      for (const p of this.pending.values()) {
        clearTimeout(p.timer);
        p.reject(e);
      }
      this.pending.clear();
    };
    this.proc.on("error", (e) => fail(e));
    this.proc.on("exit", (code) => fail(new Error(`iotcom rpc exited with code ${code}`)));
  }

  /** Sends a request and resolves with its result. */
  request<T>(method: string, params: Record<string, unknown> = {}, timeoutMs = 15000): Promise<T> {
    if (this.exited) return Promise.reject(this.exited);
    const id = this.nextId++;
    return new Promise<T>((resolve, reject) => {
      const timer = setTimeout(() => {
        this.pending.delete(id);
        reject(new Error(`${method}: no answer from iotcom within ${timeoutMs} ms`));
      }, timeoutMs);
      this.pending.set(id, { resolve: (v) => resolve(v as T), reject, timer });
      this.proc.stdin.write(JSON.stringify({ jsonrpc: "2.0", id, method, params }) + "\n");
    });
  }

  initialize(): Promise<InitializeResult> {
    return this.request("initialize");
  }

  decode(protocol: string, hex: string, direction?: "request" | "response"): Promise<Decoded> {
    return this.request("decode", { protocol, hex, ...(direction ? { direction } : {}) });
  }

  devices(): Promise<Devices> {
    return this.request("devices");
  }

  startMonitor(source: string): Promise<{ id: string; source: string }> {
    return this.request("monitor.start", { source }, 30000);
  }

  stopMonitor(id: string): Promise<{ id: string; frames: number }> {
    return this.request("monitor.stop", { id });
  }

  saveMonitor(id: string, path: string): Promise<{ path: string; frames: number }> {
    return this.request("monitor.save", { id, path });
  }

  /** Subscribes to frame notifications; returns an unsubscribe function. */
  onFrame(listener: (f: FrameEvent) => void): () => void {
    this.frameListeners.add(listener);
    return () => this.frameListeners.delete(listener);
  }

  /** Asks the CLI to exit (and kills it if it does not within a second). */
  async dispose(): Promise<void> {
    if (this.exited) return;
    try {
      await this.request("shutdown", {}, 1000);
    } catch {
      /* already gone */
    }
    if (!this.exited) this.proc.kill();
  }

  private onLine(line: string): void {
    let msg: { id?: number; result?: unknown; error?: { code: number; message: string }; method?: string; params?: unknown };
    try {
      msg = JSON.parse(line);
    } catch {
      this.log(line);
      return;
    }
    if (msg.method === "frame") {
      for (const l of this.frameListeners) l(msg.params as FrameEvent);
      return;
    }
    if (typeof msg.id !== "number") return;
    const p = this.pending.get(msg.id);
    if (!p) return;
    this.pending.delete(msg.id);
    clearTimeout(p.timer);
    if (msg.error) p.reject(new RpcError(msg.error.code, msg.error.message));
    else p.resolve(msg.result);
  }
}
