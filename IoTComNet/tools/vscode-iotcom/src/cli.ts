// Finds the iotcom CLI: the configured command, the CLI built in an IoTCom.Net checkout, or the global tool.
import { existsSync, statSync } from "fs";
import { join } from "path";

export interface CliCommand {
  command: string;
  args: string[];
  description: string;
}

/** Splits a configured command line ("dotnet C:\path with spaces\iotcom.dll") into command and arguments. */
export function splitCommandLine(line: string): string[] {
  const parts: string[] = [];
  const re = /"([^"]*)"|(\S+)/g;
  let m: RegExpExecArray | null;
  while ((m = re.exec(line)) !== null) parts.push(m[1] ?? m[2]);
  return parts;
}

/** Resolves the CLI from the setting, then a workspace build (Debug or Release), then `iotcom` on PATH. */
export function resolveCli(configured: string | undefined, workspaceFolders: readonly string[]): CliCommand {
  if (configured && configured.trim()) {
    const [command, ...args] = splitCommandLine(configured.trim());
    return { command, args, description: configured.trim() };
  }
  // A workspace build: the most recently built configuration wins (a stale Release build may lack newer commands).
  const builds = workspaceFolders
    .flatMap((root) => [root, join(root, "IoTComNet")])
    .flatMap((base) => ["Debug", "Release"].map((config) => join(base, "tools", "iotcom-cli", "bin", config, "net10.0", "iotcom.dll")))
    .filter((dll) => existsSync(dll))
    .sort((a, b) => statSync(b).mtimeMs - statSync(a).mtimeMs);
  if (builds.length > 0) return { command: "dotnet", args: [builds[0]], description: `dotnet ${builds[0]}` };
  return { command: "iotcom", args: [], description: "iotcom (global tool)" };
}
