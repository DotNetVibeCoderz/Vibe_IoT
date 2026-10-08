// IoTCom.Net Tools — VS Code extension entry point. Every decode and simulation runs in the iotcom CLI (`iotcom rpc`),
// so the extension never duplicates protocol logic. Built by Gravicode Studios, led by Kang Fadhil.
import * as vscode from "vscode";
import { resolveCli } from "./cli";
import { FrameViewer } from "./frameViewer";
import { TrafficMonitor } from "./monitor";
import { InitializeResult, IotcomRpc, ProtocolInfo } from "./rpc";
import { DeviceTree, ProtocolTree } from "./trees";

let rpc: IotcomRpc | undefined;
let rpcReady: Promise<IotcomRpc> | undefined;
let info: InitializeResult | undefined;
let output: vscode.OutputChannel;

function workspaceRoots(): string[] {
  return (vscode.workspace.workspaceFolders ?? []).map((f) => f.uri.fsPath);
}

/** Starts the CLI on first use (or after the setting changed) and caches the initialize result. */
function connect(): Promise<IotcomRpc> {
  if (rpcReady) return rpcReady;
  const cli = resolveCli(vscode.workspace.getConfiguration("iotcom").get<string>("cliPath"), workspaceRoots());
  output.appendLine(`Starting ${cli.description} rpc`);
  const client = new IotcomRpc(cli.command, cli.args, workspaceRoots()[0], (line) => output.appendLine(line));
  rpcReady = client.initialize().then(
    (result) => {
      info = result;
      rpc = client;
      output.appendLine(`iotcom ${result.version} ready — ${result.credit.en}`);
      return client;
    },
    async (e: Error) => {
      rpcReady = undefined;
      await client.dispose();
      const choice = await vscode.window.showErrorMessage(
        vscode.l10n.t('The iotcom CLI could not be started: {0}. Install it with "dotnet tool install -g IoTCom.Net.Cli --prerelease" or set iotcom.cliPath.', e.message),
        vscode.l10n.t("Open settings"));
      if (choice) void vscode.commands.executeCommand("workbench.action.openSettings", "iotcom.cliPath");
      throw e;
    });
  return rpcReady;
}

function docsLanguage(): "en" | "id" {
  const setting = vscode.workspace.getConfiguration("iotcom").get<string>("docsLanguage", "auto");
  if (setting === "en" || setting === "id") return setting;
  return vscode.env.language.startsWith("id") ? "id" : "en";
}

function docsUrl(p: ProtocolInfo): string {
  return `${info?.docsBase ?? ""}${docsLanguage()}/${p.docs}`;
}

/** Turns selected text (hex dump, 0x-prefixed list, candump line) into hex digits. */
function selectionToHex(text: string): string {
  const candump = text.trim().match(/^[0-9A-Fa-f]{3,8}#[#0-9A-Fa-fRr.]*$/);
  if (candump) return Buffer.from(text.trim()).toString("hex");
  return text.replace(/0x/gi, "").replace(/[^0-9a-fA-F]/g, "");
}

export function activate(context: vscode.ExtensionContext): void {
  output = vscode.window.createOutputChannel("IoTCom.Net");
  const protocols = new ProtocolTree();
  const devices = new DeviceTree();
  context.subscriptions.push(
    output,
    vscode.window.registerTreeDataProvider("iotcom.protocols", protocols),
    vscode.window.registerTreeDataProvider("iotcom.devices", devices));

  const refresh = async () => {
    const client = await connect();
    protocols.set(info!, docsUrl);
    devices.set(await client.devices());
  };

  const decoders = () => [...new Set((info?.protocols ?? []).flatMap((p) => p.decoders))];

  context.subscriptions.push(
    vscode.commands.registerCommand("iotcom.refresh", () => refresh().catch(() => undefined)),
    vscode.commands.registerCommand("iotcom.showOutput", () => output.show()),

    vscode.commands.registerCommand("iotcom.decodeFrame", async (protocol?: string) => {
      await connect();
      FrameViewer.show(connect, decoders(), protocol ? { protocol } : undefined);
    }),

    vscode.commands.registerCommand("iotcom.decodeSelection", async () => {
      const editor = vscode.window.activeTextEditor;
      if (!editor) return;
      const hex = selectionToHex(editor.document.getText(editor.selection));
      await connect();
      const isCandump = /^[0-9a-f]{6,16}23/i.test(hex) && editor.document.getText(editor.selection).includes("#");
      const protocol = isCandump ? "can" : await vscode.window.showQuickPick(decoders(), { placeHolder: vscode.l10n.t("Protocol of the frame") });
      if (!protocol) return;
      FrameViewer.show(connect, decoders(), { protocol, hex });
    }),

    vscode.commands.registerCommand("iotcom.startMonitor", async (source?: string) => {
      const client = await connect();
      if (!source) {
        const picked = await vscode.window.showQuickPick(info!.monitorSources, { placeHolder: vscode.l10n.t("What should the monitor watch?") });
        if (!picked) return;
        source = picked;
        if (picked === "can:<uri>") {
          const uri = await vscode.window.showInputBox({ prompt: vscode.l10n.t("CAN interface URI, e.g. socketcan:can0 or slcan:COM5"), value: "socketcan:can0" });
          if (!uri) return;
          source = "can:" + uri;
        } else if (picked === "mavlink:udp:<port>") {
          const port = await vscode.window.showInputBox({ prompt: vscode.l10n.t("UDP port of the MAVLink stream (e.g. 14550)"), value: "14550" });
          if (!port) return;
          source = "mavlink:udp:" + port;
        }
      }
      try {
        await TrafficMonitor.start(client, source);
      } catch (e) {
        void vscode.window.showErrorMessage((e as Error).message);
      }
    }),

    vscode.commands.registerCommand("iotcom.openDocs", async (target: string) => {
      if (target.startsWith("notebook:")) {
        const rel = `notebooks/${target.slice(9)}.${docsLanguage()}.ipynb`;
        for (const root of workspaceRoots()) {
          for (const base of [root, `${root}/IoTComNet`]) {
            const uri = vscode.Uri.file(`${base}/${rel}`);
            try {
              await vscode.workspace.fs.stat(uri);
              await vscode.commands.executeCommand("vscode.open", uri);
              return;
            } catch {
              /* try the next location */
            }
          }
        }
        target = `${info?.docsBase.replace(/docs\/$/, "") ?? ""}${rel}`;
      }
      await vscode.env.openExternal(vscode.Uri.parse(target));
    }),

    vscode.commands.registerCommand("iotcom.copyInstall", async (pkg: string) => {
      const command = `dotnet add package ${pkg} --prerelease`;
      await vscode.env.clipboard.writeText(command);
      void vscode.window.showInformationMessage(vscode.l10n.t("Copied: {0}", command));
    }),

    vscode.workspace.onDidChangeConfiguration(async (e) => {
      if (!e.affectsConfiguration("iotcom.cliPath")) return;
      await rpc?.dispose();
      rpc = undefined;
      rpcReady = undefined;
      void refresh().catch(() => undefined);
    }));

  void refresh().catch(() => undefined);
}

export async function deactivate(): Promise<void> {
  await rpc?.dispose();
}
