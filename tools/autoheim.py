#!/usr/bin/env python3
"""autoheim — launch and drive an automated Valheim client.

Launcher subcommands:
  autoheim setup    [--instance N]            build/refresh the isolated BepInEx profile
  autoheim launch   [--instance N] [--port P] [--window WxH] [--fps F] [--savedir D] [--no-wait] [-- extra game args]
  autoheim kill     [--instance N]            stop the game process (SIGTERM, then SIGKILL)
  autoheim ps                                 list instances and whether they are running
  autoheim deploy   [--instance N]            dotnet build + copy AutoHeim.dll into the profile (restart to load)
  autoheim paths    [--instance N]            show profile, save, log and output locations
  autoheim tail     [--instance N] [-n 50]    print the end of the BepInEx log file
  autoheim server-start [--port 2476] [--world AutoSrv] [--password secret12] [--crossplay]
                                              start a throwaway vanilla dedicated server (own save dir, no mods)
                                              and wait until it accepts players
  autoheim server-stop                        stop that server cleanly

Anything else is an API command sent to the running client:
  autoheim status
  autoheim create_character name=Tester
  autoheim start_world world=TestWorld character=Tester
  autoheim move_to x=10 z=-20 run=true
  autoheim help                     (lists every API command)
Values are parsed as JSON when possible (10, true, "x"), otherwise kept as strings.

Global options (any position before the command): --instance N (default "default"), --port P, --raw
Environment: AUTOHEIM_HOME (default ~/.local/share/AutoHeim), VALHEIM_CLIENT_DIR.
Exit status is 0 when the API answered ok=true, 1 otherwise.
"""
import json
import os
import shutil
import signal
import socket
import subprocess
import sys
import time
import urllib.error
import urllib.request
from pathlib import Path

REPO = Path(__file__).resolve().parent.parent
HOME = Path(os.environ.get("AUTOHEIM_HOME", Path.home() / ".local/share/AutoHeim"))
GAME = Path(os.environ.get("VALHEIM_CLIENT_DIR", Path.home() / ".steam/steam/steamapps/common/Valheim"))
SERVER = Path(os.environ.get("VALHEIM_SERVER_DIR", Path.home() / ".steam/steam/steamapps/common/Valheim dedicated server"))
PLUGIN_DLL = REPO / "bin/Release/netstandard2.1/AutoHeim.dll"
DEFAULT_PORT = 7878
STEAM_APP_ID = "892970"


# ---------------------------------------------------------------- paths & state

def inst_dir(name):
    return HOME / "instances" / name


def run_file(name):
    return inst_dir(name) / "run.json"


def read_run(name):
    try:
        return json.loads(run_file(name).read_text())
    except (OSError, ValueError):
        return None


def pid_alive(pid):
    try:
        os.kill(pid, 0)
    except ProcessLookupError:
        return False
    except PermissionError:
        return True
    # Zombie or reused pid check: make sure it is still valheim.
    try:
        return "valheim" in Path(f"/proc/{pid}/cmdline").read_bytes().decode(errors="replace")
    except OSError:
        return False


def resolve_port(opts):
    if opts.get("port"):
        return int(opts["port"])
    run = read_run(opts["instance"])
    if run and run.get("port"):
        return int(run["port"])
    if os.environ.get("AUTOHEIM_PORT"):
        return int(os.environ["AUTOHEIM_PORT"])
    return DEFAULT_PORT


# ---------------------------------------------------------------- API client

def api(port, cmd, args=None, timeout=None):
    body = json.dumps(args or {}).encode()
    req = urllib.request.Request(f"http://127.0.0.1:{port}/{cmd}", data=body,
                                 headers={"Content-Type": "application/json"}, method="POST")
    wait = timeout
    if wait is None:
        t = (args or {}).get("timeout")
        s = (args or {}).get("seconds")
        wait = max(float(t) if isinstance(t, (int, float)) else 240.0,
                   float(s) if isinstance(s, (int, float)) else 0.0) + 30.0
    try:
        with urllib.request.urlopen(req, timeout=wait) as r:
            return json.loads(r.read())
    except urllib.error.HTTPError as e:
        try:
            return json.loads(e.read())
        except ValueError:
            return {"ok": False, "cmd": cmd, "error": f"HTTP {e.code}"}
    except (urllib.error.URLError, ConnectionError, socket.timeout) as e:
        return {"ok": False, "cmd": cmd, "error": f"cannot reach AutoHeim on port {port}: {e}. Is the client running? (autoheim launch)"}


def parse_value(v):
    try:
        return json.loads(v)
    except ValueError:
        return v


# ---------------------------------------------------------------- setup / launch

def setup(name, quiet=False):
    if not (GAME / "valheim.x86_64").exists():
        sys.exit(f"Valheim client not found at {GAME} (set VALHEIM_CLIENT_DIR)")
    src_core = GAME / "BepInEx/core"
    if not (src_core / "BepInEx.Preloader.dll").exists():
        sys.exit(f"BepInEx not installed in {GAME} (need BepInEx/core/BepInEx.Preloader.dll)")
    if not PLUGIN_DLL.exists():
        build()

    root = inst_dir(name) / "BepInEx"
    core = root / "core"
    if core.exists():
        shutil.rmtree(core)
    shutil.copytree(src_core, core)
    plugins = root / "plugins" / "AutoHeim"
    plugins.mkdir(parents=True, exist_ok=True)
    shutil.copy2(PLUGIN_DLL, plugins / "AutoHeim.dll")
    pdb = PLUGIN_DLL.with_suffix(".pdb")
    if pdb.exists():
        shutil.copy2(pdb, plugins / "AutoHeim.pdb")
    (root / "config").mkdir(exist_ok=True)
    # Same BepInEx settings as the main install (console/log options), minus anything instance-specific.
    src_cfg = GAME / "BepInEx/config/BepInEx.cfg"
    if src_cfg.exists() and not (root / "config/BepInEx.cfg").exists():
        shutil.copy2(src_cfg, root / "config/BepInEx.cfg")
    (HOME / "saves").mkdir(parents=True, exist_ok=True)
    (inst_dir(name) / "output").mkdir(exist_ok=True)
    if not quiet:
        print(json.dumps({"ok": True, "profile": str(root), "plugin": str(plugins / "AutoHeim.dll"),
                          "extra_plugins_dir": str(root / "plugins"),
                          "note": "drop other mod DLLs into extra_plugins_dir to test them alongside AutoHeim"}, indent=2))


def build():
    r = subprocess.run(["dotnet", "build", "-c", "Release", "-nologo", "-v", "q", str(REPO / "AutoHeim.csproj")],
                       capture_output=True, text=True)
    if r.returncode != 0:
        sys.stderr.write(r.stdout + r.stderr)
        sys.exit("dotnet build failed")


def port_free(port):
    with socket.socket() as s:
        return s.connect_ex(("127.0.0.1", port)) != 0


def launch(name, opts, extra):
    run = read_run(name)
    if run and pid_alive(run["pid"]):
        sys.exit(f"instance '{name}' already running (pid {run['pid']}, port {run['port']}); 'autoheim kill' first")
    port = int(opts.get("port") or DEFAULT_PORT)
    if not port_free(port):
        sys.exit(f"port {port} is in use; pass --port")
    if subprocess.run(["pgrep", "-x", "steam"], capture_output=True).returncode != 0:
        print("warning: Steam does not appear to be running; Valheim needs it for SteamAPI_Init", file=sys.stderr)

    setup(name, quiet=True)
    d = inst_dir(name)
    savedir = Path(opts.get("savedir") or HOME / "saves").resolve()
    savedir.mkdir(parents=True, exist_ok=True)
    unity_log = d / "Player.log"
    stdout_log = d / "stdout.log"

    env = dict(os.environ)
    env.update({
        "DOORSTOP_ENABLED": "1",
        "DOORSTOP_TARGET_ASSEMBLY": str(d / "BepInEx/core/BepInEx.Preloader.dll"),
        "DOORSTOP_IGNORE_DISABLED_ENV": "0",
        "DOORSTOP_MONO_DEBUG_ENABLED": "0",
        "LD_LIBRARY_PATH": f"{GAME}/:{env.get('LD_LIBRARY_PATH', '')}",
        "LD_PRELOAD": f"{GAME}/libdoorstop.so" + (f":{env['LD_PRELOAD']}" if env.get("LD_PRELOAD") else ""),
        "SteamAppId": STEAM_APP_ID,
        "SteamGameId": STEAM_APP_ID,
        "AUTOHEIM_PORT": str(port),
        "AUTOHEIM_SAVEDIR": str(savedir),
        "AUTOHEIM_FPS": str(opts.get("fps") or 60),
    })
    window = opts.get("window", "1280x720")
    if window and window != "none":
        env["AUTOHEIM_WINDOW"] = window
    cfg = d / "BepInEx/config/tarbaby.autoheim.cfg"
    if not cfg.exists():
        cfg.write_text(f"[General]\nOutputDir = {d / 'output'}\n")

    args = [str(GAME / "valheim.x86_64"), "-console", "-logFile", str(unity_log)]
    if window and window != "none":
        w, h = window.lower().split("x")
        args += ["-screen-fullscreen", "0", "-screen-width", w, "-screen-height", h, "-window-mode", "windowed"]
    args += extra

    with open(stdout_log, "wb") as out:
        proc = subprocess.Popen(args, cwd=GAME, env=env, stdout=out, stderr=subprocess.STDOUT,
                                stdin=subprocess.DEVNULL, start_new_session=True)
    run_file(name).write_text(json.dumps({"pid": proc.pid, "port": port, "started": time.time(),
                                          "savedir": str(savedir)}))

    result = {"ok": True, "instance": name, "pid": proc.pid, "port": port, "savedir": str(savedir),
              "bepinex_log": str(d / "BepInEx/LogOutput.log"), "unity_log": str(unity_log)}
    if opts.get("no_wait"):
        print(json.dumps(result, indent=2))
        return 0
    deadline = time.time() + float(opts.get("wait_secs") or 180)
    while time.time() < deadline:
        if proc.poll() is not None:
            result.update(ok=False, error=f"game exited during startup (code {proc.returncode}); see {stdout_log} and {unity_log}")
            print(json.dumps(result, indent=2))
            return 1
        r = api(port, "ping", timeout=2)
        if r.get("ok"):
            # Wait for the main menu so the next command can act immediately.
            s = api(port, "wait", {"state": "menu", "timeout": max(5, deadline - time.time())})
            result["state"] = (s.get("result") or {}).get("state") if s.get("ok") else s.get("error")
            print(json.dumps(result, indent=2))
            return 0 if s.get("ok") else 1
        time.sleep(1)
    result.update(ok=False, error="API did not come up in time; check the logs")
    print(json.dumps(result, indent=2))
    return 1


def kill(name):
    run = read_run(name)
    if not run or not pid_alive(run["pid"]):
        print(json.dumps({"ok": True, "instance": name, "running": False}))
        return 0
    pid = run["pid"]
    try:
        os.killpg(pid, signal.SIGTERM)
    except ProcessLookupError:
        pass
    for _ in range(50):
        if not pid_alive(pid):
            break
        time.sleep(0.2)
    else:
        try:
            os.killpg(pid, signal.SIGKILL)
        except ProcessLookupError:
            pass
    print(json.dumps({"ok": True, "instance": name, "killed": pid}))
    return 0


def ps():
    rows = []
    for d in sorted((HOME / "instances").glob("*")) if (HOME / "instances").exists() else []:
        run = read_run(d.name) or {}
        alive = bool(run.get("pid")) and pid_alive(run["pid"])
        rows.append({"instance": d.name, "running": alive, "pid": run.get("pid") if alive else None,
                     "port": run.get("port")})
    print(json.dumps(rows, indent=2))
    return 0


def paths(name):
    d = inst_dir(name)
    run = read_run(name) or {}
    print(json.dumps({
        "game": str(GAME), "home": str(HOME), "profile": str(d / "BepInEx"),
        "plugins": str(d / "BepInEx/plugins"), "saves": run.get("savedir", str(HOME / "saves")),
        "output": str(d / "output"), "bepinex_log": str(d / "BepInEx/LogOutput.log"),
        "unity_log": str(d / "Player.log"), "plugin_build": str(PLUGIN_DLL),
    }, indent=2))
    return 0


def tail(name, n):
    log = inst_dir(name) / "BepInEx/LogOutput.log"
    if not log.exists():
        sys.exit(f"no log at {log}")
    lines = log.read_text(errors="replace").splitlines()
    print("\n".join(lines[-n:]))
    return 0


# ---------------------------------------------------------------- throwaway dedicated server

def server_start(opts):
    d = HOME / "server"
    d.mkdir(parents=True, exist_ok=True)
    run = d / "run.json"
    if run.exists():
        info = json.loads(run.read_text())
        if pid_alive_server(info["pid"]):
            print(json.dumps({"ok": True, "already_running": True, **info}, indent=2))
            return 0
    exe = SERVER / "valheim_server.x86_64"
    if not exe.exists():
        sys.exit(f"dedicated server not found at {SERVER} (set VALHEIM_SERVER_DIR)")
    port = int(opts.get("port") or 2476)
    world = opts.get("world") or "AutoSrv"
    password = opts.get("password") or "secret12"
    log = d / "server.log"
    env = dict(os.environ, SteamAppId=STEAM_APP_ID, LD_LIBRARY_PATH=f"{SERVER}/linux64:{os.environ.get('LD_LIBRARY_PATH', '')}")
    for k in [k for k in env if k.startswith("DOORSTOP") or k == "LD_PRELOAD"]:
        env.pop(k)
    args = [str(exe), "-nographics", "-batchmode", "-name", f"AutoHeim {world}", "-port", str(port),
            "-world", world, "-password", password, "-public", "0", "-savedir", str(d / "saves")]
    if opts.get("crossplay"):
        args.append("-crossplay")
    with open(log, "wb") as out:
        proc = subprocess.Popen(args, cwd=SERVER, env=env, stdout=out, stderr=subprocess.STDOUT,
                                stdin=subprocess.DEVNULL, start_new_session=True)
    info = {"pid": proc.pid, "address": f"127.0.0.1:{port}", "world": world, "password": password, "log": str(log)}
    run.write_text(json.dumps(info))
    # "Loading: Done" is logged once world generation finishes; joining before that times out.
    deadline = time.time() + float(opts.get("wait_secs") or 240)
    while time.time() < deadline:
        if proc.poll() is not None:
            print(json.dumps({"ok": False, "error": f"server exited (code {proc.returncode}); see {log}", **info}, indent=2))
            return 1
        if "Loading: Done" in log.read_text(errors="replace"):
            print(json.dumps({"ok": True, **info, "join": f"autoheim join_server address={info['address']} password={password}"}, indent=2))
            return 0
        time.sleep(1)
    print(json.dumps({"ok": False, "error": "server did not finish loading in time", **info}, indent=2))
    return 1


def pid_alive_server(pid):
    try:
        return "valheim_server" in Path(f"/proc/{pid}/cmdline").read_bytes().decode(errors="replace")
    except OSError:
        return False


def server_stop():
    run = HOME / "server" / "run.json"
    if not run.exists():
        print(json.dumps({"ok": True, "running": False}))
        return 0
    pid = json.loads(run.read_text())["pid"]
    if pid_alive_server(pid):
        os.kill(pid, signal.SIGINT)  # SIGINT makes the server save and exit
        for _ in range(150):
            if not pid_alive_server(pid):
                break
            time.sleep(0.2)
        else:
            os.kill(pid, signal.SIGKILL)
    run.unlink()
    print(json.dumps({"ok": True, "stopped": pid}))
    return 0


# ---------------------------------------------------------------- main

def main(argv):
    opts = {"instance": "default"}
    rest = []
    extra = []
    i = 0
    while i < len(argv):
        a = argv[i]
        if a == "--":
            extra = argv[i + 1:]
            break
        if a in ("--instance", "--port", "--window", "--fps", "--savedir", "--wait-secs", "-n",
                 "--world", "--password") and i + 1 < len(argv):
            opts[a.lstrip("-").replace("-", "_")] = argv[i + 1]
            i += 2
            continue
        if a in ("--no-wait", "--raw", "--crossplay"):
            opts[a.lstrip("-").replace("-", "_")] = True
            i += 1
            continue
        if a in ("-h", "--help") and not rest:
            print(__doc__)
            return 0
        rest.append(a)
        i += 1

    if not rest:
        print(__doc__)
        return 0
    cmd, params = rest[0], rest[1:]
    name = opts["instance"]

    if cmd == "setup":
        setup(name)
        return 0
    if cmd == "launch":
        return launch(name, opts, extra)
    if cmd == "kill":
        return kill(name)
    if cmd == "ps":
        return ps()
    if cmd == "paths":
        return paths(name)
    if cmd == "tail":
        return tail(name, int(opts.get("n", 50)))
    if cmd == "server-start":
        return server_start(opts)
    if cmd == "server-stop":
        return server_stop()
    if cmd == "deploy":
        build()
        setup(name)
        return 0

    args = {}
    for p in params:
        if "=" not in p:
            sys.exit(f"argument '{p}' must be key=value")
        k, v = p.split("=", 1)
        args[k] = parse_value(v)
    resp = api(resolve_port(opts), cmd, args)
    if opts.get("raw"):
        print(json.dumps(resp))
    else:
        print(json.dumps(resp, indent=2))
    return 0 if resp.get("ok") else 1


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
