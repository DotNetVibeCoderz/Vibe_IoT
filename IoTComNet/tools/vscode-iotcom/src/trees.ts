// Protocol Explorer and Devices tree views.
import * as vscode from "vscode";
import type { Devices, InitializeResult, ProtocolInfo } from "./rpc";

type Node = { item: vscode.TreeItem; children?: Node[] };

function leaf(label: string, icon: string, description?: string, command?: vscode.Command, tooltip?: string): Node {
  const item = new vscode.TreeItem(label, vscode.TreeItemCollapsibleState.None);
  item.iconPath = new vscode.ThemeIcon(icon);
  item.description = description;
  item.command = command;
  item.tooltip = tooltip;
  return { item };
}

function branch(label: string, icon: string, children: Node[], description?: string, expanded = false): Node {
  const item = new vscode.TreeItem(label, expanded ? vscode.TreeItemCollapsibleState.Expanded : vscode.TreeItemCollapsibleState.Collapsed);
  item.iconPath = new vscode.ThemeIcon(icon);
  item.description = description;
  return { item, children };
}

abstract class Tree implements vscode.TreeDataProvider<Node> {
  protected readonly changed = new vscode.EventEmitter<void>();
  readonly onDidChangeTreeData = this.changed.event;
  protected roots: Node[] = [];

  getTreeItem(n: Node): vscode.TreeItem {
    return n.item;
  }

  getChildren(n?: Node): Node[] {
    return n ? n.children ?? [] : this.roots;
  }
}

/** Protocols → package, documentation, notebook, sample and frame decoders. */
export class ProtocolTree extends Tree {
  set(info: InitializeResult, docsUrl: (p: ProtocolInfo) => string): void {
    this.roots = info.protocols.map((p) =>
      branch(p.name, "symbol-interface", [
        leaf(vscode.l10n.t("Package {0}", p.package), "package", undefined,
          { command: "iotcom.copyInstall", title: "", arguments: [p.package] }, `dotnet add package ${p.package} --prerelease`),
        leaf(vscode.l10n.t("Documentation"), "book", p.docs, { command: "iotcom.openDocs", title: "", arguments: [docsUrl(p)] }),
        ...(p.notebook ? [leaf(vscode.l10n.t("Notebook {0}", p.notebook), "notebook", undefined,
          { command: "iotcom.openDocs", title: "", arguments: [`notebook:${p.notebook}`] })] : []),
        ...(p.sample ? [leaf(vscode.l10n.t("Sample {0}", p.sample), "terminal", `samples/console/${p.sample}`)] : []),
        ...p.decoders.map((d) => leaf(vscode.l10n.t("Decode as {0}", d), "symbol-structure", undefined,
          { command: "iotcom.decodeFrame", title: "", arguments: [d] })),
      ], p.roles));
    this.changed.fire();
  }
}

/** Serial ports, CAN interfaces and built-in simulators; simulators and CAN interfaces start a monitor on click. */
export class DeviceTree extends Tree {
  set(d: Devices): void {
    const watch = (source: string): vscode.Command => ({ command: "iotcom.startMonitor", title: vscode.l10n.t("Watch this source"), arguments: [source] });
    const none = () => [leaf(vscode.l10n.t("none found"), "circle-slash")];
    this.roots = [
      branch(vscode.l10n.t("Simulators"), "beaker", d.simulators.map((s) => leaf(s.slice(4), "play", s, watch(s), vscode.l10n.t("Watch this source"))), undefined, true),
      branch(vscode.l10n.t("CAN interfaces"), "circuit-board", d.canInterfaces.length ? d.canInterfaces.map((c) => leaf(c, "pulse", undefined, watch("can:" + c))) : none(), undefined, true),
      branch(vscode.l10n.t("Serial ports"), "plug", d.serialPorts.length ? d.serialPorts.map((p) => leaf(p, "plug")) : none(), undefined, true),
    ];
    this.changed.fire();
  }
}
