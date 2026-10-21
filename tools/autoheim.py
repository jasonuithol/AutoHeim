#!/usr/bin/env python3
r"""autoheim — launch and drive an automated Valheim client.

Launcher subcommands:
  autoheim setup    [--instance N]            build/refresh the isolated BepInEx profile
  autoheim launch   [--instance N] [--port P] [--window WxH] [--fps F] [--savedir D] [--no-wait] [-- extra game args]
  autoheim kill     [--instance N]            stop the game process (graceful, then forced)
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
Environment:
  AUTOHEIM_HOME        state dir (Linux: ~/.local/share/AutoHeim, Windows: %LOCALAPPDATA%\AutoHeim)
  VALHEIM_CLIENT_DIR   game install; found automatically in any Steam library if unset
  VALHEIM_SERVER_DIR   dedicated server install; likewise
Works on Linux and Windows (on Windows run it as `py tools\autoheim.py ...` or `tools\autoheim.cmd ...`).
Exit status is 0 when the API answered ok=true, 1 otherwise.
"""
import json
import os
import re
import shutil
import signal
import socket
import subprocess
import sys
import time
import urllib.error
import urllib.request
from pathlib import Path

WINDOWS = os.name == "nt"
REPO = Path(__file__).resolve().parent.parent
PLUGIN_DLL = REPO / "bin/Release/netstandard2.1/AutoHeim.dll"
DEFAULT_PORT = 7878
STEAM_APP_ID = "892970"
CLIENT_EXE = "valheim.exe" if WINDOWS else "valheim.x86_64"
SERVER_EXE = "valheim_server.exe" if WINDOWS else "valheim_server.x86_64"


def _default_home():
    if os.environ.get("AUTOHEIM_HOME"):
        return Path(os.environ["AUTOHEIM_HOME"])
    if WINDOWS:
        return Path(os.environ.get("LOCALAPPDATA", Path.home() / "AppData/Local")) / "AutoHeim"
    return Path(os.environ.get("XDG_DATA_HOME", Path.home() / ".local/share")) / "AutoHeim"


HOME = _default_home()


# ---------------------------------------------------------------- platform: finding Steam installs

def steam_roots():
    """Steam installation directories that exist on this machine."""
    candidates = []
    if WINDOWS:
        import winreg
        for hive, key, value in ((winreg.HKEY_CURRENT_USER, r"Software\Valve\Steam", "SteamPath"),
                                 (winreg.HKEY_LOCAL_MACHINE, r"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath"),
                                 (winreg.HKEY_LOCAL_MACHINE, r"SOFTWARE\Valve\Steam", "InstallPath")):
            try:
                with winreg.OpenKey(hive, key) as k:
                    candidates.append(Path(winreg.QueryValueEx(k, value)[0]))
            except OSError:
                pass
        for env in ("ProgramFiles(x86)", "ProgramFiles"):
            if os.environ.get(env):
                candidates.append(Path(os.environ[env]) / "Steam")
    else:
        h = Path.home()
        candidates += [h / ".steam/steam", h / ".local/share/Steam", h / ".steam/debian-installation",
                       h / ".var/app/com.valvesoftware.Steam/.local/share/Steam"]
    return _unique(c for c in candidates if (c / "steamapps").is_dir())


def steam_libraries():
    """Every Steam library folder: the roots plus the extra libraries listed in libraryfolders.vdf."""
    libs = []
    for root in steam_roots():
        libs.append(root)
        vdf = root / "steamapps/libraryfolders.vdf"
        try:
            text = vdf.read_text(errors="replace")
        except OSError:
            continue
        for m in re.finditer(r'"path"\s+"([^"]+)"', text):
            libs.append(Path(m.group(1).replace("\\\\", "\\")))
    return _unique(l for l in libs if (l / "steamapps").is_dir())


def _unique(paths):
    seen, out = set(), []
    for p in paths:
        key = os.path.normcase(str(p.resolve()))
        if key not in seen:
            seen.add(key)
            out.append(p)
    return out


def find_app(env_var, folder, exe):
    if os.environ.get(env_var):
        return Path(os.environ[env_var])
    for lib in steam_libraries():
        d = lib / "steamapps/common" / folder
        if (d / exe).exists():
            return d
    return None


def game_dir():
    d = find_app("VALHEIM_CLIENT_DIR", "Valheim", CLIENT_EXE)
    if d is None or not (d / CLIENT_EXE).exists():
        libs = ", ".join(str(l) for l in steam_libraries()) or "none found"
        sys.exit(f"Valheim client ({CLIENT_EXE}) not found. Searched Steam libraries: {libs}. Set VALHEIM_CLIENT_DIR.")
    return d


def server_dir():
    d = find_app("VALHEIM_SERVER_DIR", "Valheim dedicated server", SERVER_EXE)
    if d is None or not (d / SERVER_EXE).exists():
        sys.exit(f"Valheim dedicated server ({SERVER_EXE}) not found in any Steam library. "
                 "Install 'Valheim Dedicated Server' from Steam (Tools) or set VALHEIM_SERVER_DIR.")
    return d


# ---------------------------------------------------------------- platform: processes

def spawn(args, cwd, env, log_path):
    """Start a detached process with stdout/stderr going to log_path."""
    with open(log_path, "wb") as out:
        kw = dict(cwd=cwd, env=env, stdout=out, stderr=subprocess.STDOUT, stdin=subprocess.DEVNULL)
        if WINDOWS:
            kw["creationflags"] = 0x00000200 | 0x00000008  # CREATE_NEW_PROCESS_GROUP | DETACHED_PROCESS
        else:
            kw["start_new_session"] = True
        return subprocess.Popen(args, **kw)


def proc_running(pid, exe_name):
    """True if pid is alive and is the given executable (guards against pid reuse)."""
    if not pid:
        return False
    if WINDOWS:
        import ctypes
        from ctypes import wintypes
        k32 = ctypes.windll.kernel32
        h = k32.OpenProcess(0x1000, False, int(pid))  # PROCESS_QUERY_LIMITED_INFORMATION
        if not h:
            return False
        try:
            code = wintypes.DWORD()
            if not k32.GetExitCodeProcess(h, ctypes.byref(code)) or code.value != 259:  # STILL_ACTIVE
                return False
            buf = ctypes.create_unicode_buffer(1024)
            size = wintypes.DWORD(len(buf))
            if not k32.QueryFullProcessImageNameW(h, 0, buf, ctypes.byref(size)):
                return True
            return buf.value.lower().endswith(exe_name.lower())
        finally:
            k32.CloseHandle(h)
    try:
        return exe_name in Path(f"/proc/{pid}/cmdline").read_bytes().decode(errors="replace")
    except OSError:
        pass
    try:  # no /proc (e.g. macOS): best effort
        os.kill(pid, 0)
        return True
    except OSError:
        return False


def stop_process(pid, exe_name, graceful_secs=10.0):
    """Ask the process to exit (SIGINT/SIGTERM or taskkill), then force it."""
    if WINDOWS:
        subprocess.run(["taskkill", "/PID", str(pid), "/T"], capture_output=True)
    else:
        try:
            os.killpg(pid, signal.SIGINT if exe_name == SERVER_EXE else signal.SIGTERM)
        except (ProcessLookupError, PermissionError):
            try:
                os.kill(pid, signal.SIGTERM)
            except ProcessLookupError:
                return
    deadline = time.time() + graceful_secs
    while time.time() < deadline:
        if not proc_running(pid, exe_name):
            return
        time.sleep(0.2)
    if WINDOWS:
        subprocess.run(["taskkill", "/PID", str(pid), "/T", "/F"], capture_output=True)
    else:
        try:
            os.killpg(pid, signal.SIGKILL)
        except (ProcessLookupError, PermissionError):
            pass


def steam_running():
    try:
        if WINDOWS:
            r = subprocess.run(["tasklist", "/FI", "IMAGENAME eq steam.exe", "/NH"], capture_output=True, text=True)
            return "steam.exe" in r.stdout.lower()
        return subprocess.run(["pgrep", "-x", "steam"], capture_output=True).returncode == 0
    except FileNotFoundError:
        return True  # can't tell; don't nag


def doorstop_args(game, preloader):
    """Windows Doorstop is configured by command-line switches (names differ between v3 and v4)."""
    try:
        v4 = (game / ".doorstop_version").read_text().strip().startswith("4")
    except OSError:
        v4 = True  # current BepInExPack_Valheim ships Doorstop 4
    if v4:
        return ["--doorstop-enabled", "true", "--doorstop-target-assembly", str(preloader)]
    return ["--doorstop-enable", "true", "--doorstop-target", str(preloader)]


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
    GAME = game_dir()
    src_core = GAME / "BepInEx/core"
    if not (src_core / "BepInEx.Preloader.dll").exists():
        sys.exit(f"BepInEx not installed in {GAME} (need BepInEx/core/BepInEx.Preloader.dll). "
                 "Install BepInExPack_Valheim into the game folder first.")
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
    # Reference assemblies come from the game install, wherever it is on this machine.
    r = subprocess.run(["dotnet", "build", "-c", "Release", "-nologo", "-v", "q", f"-p:ValheimDir={game_dir()}",
                        str(REPO / "AutoHeim.csproj")], capture_output=True, text=True)
    if r.returncode != 0:
        sys.stderr.write(r.stdout + r.stderr)
        sys.exit("dotnet build failed")


def port_free(port):
    with socket.socket() as s:
        return s.connect_ex(("127.0.0.1", port)) != 0


def launch(name, opts, extra):
    run = read_run(name)
    if run and proc_running(run["pid"], CLIENT_EXE):
        sys.exit(f"instance '{name}' already running (pid {run['pid']}, port {run['port']}); 'autoheim kill' first")
    port = int(opts.get("port") or DEFAULT_PORT)
    if not port_free(port):
        sys.exit(f"port {port} is in use; pass --port")
    if not steam_running():
        print("warning: Steam does not appear to be running; Valheim needs it for SteamAPI_Init", file=sys.stderr)

    setup(name, quiet=True)
    GAME = game_dir()
    d = inst_dir(name)
    preloader = d / "BepInEx/core/BepInEx.Preloader.dll"
    savedir = Path(opts.get("savedir") or HOME / "saves").resolve()
    savedir.mkdir(parents=True, exist_ok=True)
    unity_log = d / "Player.log"
    stdout_log = d / "stdout.log"

    env = dict(os.environ)
    if not WINDOWS:
        # Linux Doorstop is an LD_PRELOAD library configured through the environment.
        env.update({
            "DOORSTOP_ENABLED": "1",
            "DOORSTOP_TARGET_ASSEMBLY": str(preloader),
            "DOORSTOP_IGNORE_DISABLED_ENV": "0",
            "DOORSTOP_MONO_DEBUG_ENABLED": "0",
            "LD_LIBRARY_PATH": f"{GAME}/:{env.get('LD_LIBRARY_PATH', '')}",
            "LD_PRELOAD": f"{GAME}/libdoorstop.so" + (f":{env['LD_PRELOAD']}" if env.get("LD_PRELOAD") else ""),
        })
    env.update({
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
        # Forward slashes: BepInEx config strings treat backslashes as escapes.
        cfg.write_text(f"[General]\nOutputDir = {(d / 'output').as_posix()}\n")

    args = [str(GAME / CLIENT_EXE)]
    if WINDOWS:
        args += doorstop_args(GAME, preloader)
    args += ["-console", "-logFile", str(unity_log)]
    if window and window != "none":
        w, h = window.lower().split("x")
        args += ["-screen-fullscreen", "0", "-screen-width", w, "-screen-height", h, "-window-mode", "windowed"]
    args += extra

    proc = spawn(args, GAME, env, stdout_log)
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
    if not run or not proc_running(run["pid"], CLIENT_EXE):
        print(json.dumps({"ok": True, "instance": name, "running": False}))
        return 0
    pid = run["pid"]
    stop_process(pid, CLIENT_EXE)
    print(json.dumps({"ok": True, "instance": name, "killed": pid}))
    return 0


def ps():
    rows = []
    for d in sorted((HOME / "instances").glob("*")) if (HOME / "instances").exists() else []:
        run = read_run(d.name) or {}
        alive = bool(run.get("pid")) and proc_running(run["pid"], CLIENT_EXE)
        rows.append({"instance": d.name, "running": alive, "pid": run.get("pid") if alive else None,
                     "port": run.get("port")})
    print(json.dumps(rows, indent=2))
    return 0


def paths(name):
    d = inst_dir(name)
    run = read_run(name) or {}
    print(json.dumps({
        "platform": "windows" if WINDOWS else "linux", "game": str(game_dir()), "home": str(HOME),
        "steam_libraries": [str(l) for l in steam_libraries()], "profile": str(d / "BepInEx"),
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
        if proc_running(info["pid"], SERVER_EXE):
            print(json.dumps({"ok": True, "already_running": True, **info}, indent=2))
            return 0
    SERVER = server_dir()
    exe = SERVER / SERVER_EXE
    port = int(opts.get("port") or 2476)
    world = opts.get("world") or "AutoSrv"
    password = opts.get("password") or "secret12"
    log = d / "server.log"
    env = dict(os.environ, SteamAppId=STEAM_APP_ID)
    if not WINDOWS:
        env["LD_LIBRARY_PATH"] = f"{SERVER}/linux64:{os.environ.get('LD_LIBRARY_PATH', '')}"
    for k in [k for k in env if k.startswith("DOORSTOP") or k == "LD_PRELOAD"]:
        env.pop(k)  # vanilla server: never inherit a mod loader
    args = [str(exe), "-nographics", "-batchmode", "-name", f"AutoHeim {world}", "-port", str(port),
            "-world", world, "-password", password, "-public", "0", "-savedir", str(d / "saves")]
    if opts.get("crossplay"):
        args.append("-crossplay")
    proc = spawn(args, SERVER, env, log)
    info = {"pid": proc.pid, "address": f"127.0.0.1:{port}", "world": world, "password": password, "log": str(log)}
    run.write_text(json.dumps(info))
    # Joining before the server is ready times out. It is ready once "Game server connected"
    # is logged and, when it is generating a new world, after location generation reports done
    # ("sinze" is the game's own spelling). An earlier "Loading: Done, Total time" line is unrelated.
    deadline = time.time() + float(opts.get("wait_secs") or 240)
    while time.time() < deadline:
        if proc.poll() is not None:
            print(json.dumps({"ok": False, "error": f"server exited (code {proc.returncode}); see {log}", **info}, indent=2))
            return 1
        text = log.read_text(errors="replace")
        generating = "Generating locations" in text
        if "Game server connected" in text and (not generating or "Total seconds sinze ZNet start" in text):
            print(json.dumps({"ok": True, **info, "join": f"autoheim join_server address={info['address']} password={password}"}, indent=2))
            return 0
        time.sleep(1)
    print(json.dumps({"ok": False, "error": "server did not finish loading in time", **info}, indent=2))
    return 1


def server_stop():
    run = HOME / "server" / "run.json"
    if not run.exists():
        print(json.dumps({"ok": True, "running": False}))
        return 0
    pid = json.loads(run.read_text())["pid"]
    if proc_running(pid, SERVER_EXE):
        # Linux: SIGINT makes the server save and exit. Windows: a detached server has no console
        # to receive Ctrl+C, so it is ended with taskkill (fine for a throwaway world).
        stop_process(pid, SERVER_EXE, graceful_secs=30.0)
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
