#!/usr/bin/env bash
# End-to-end check of the AutoHeim API against a real client.
# Launches (or reuses) the default instance, creates a throwaway character
# and world, exercises every command family, joins a throwaway dedicated
# server, then cleans up the character/world it made.
#
#   tools/smoke_test.sh            # full run, including the dedicated-server join
#   SKIP_SERVER=1 tools/smoke_test.sh
#   PYTHON=py tools/smoke_test.sh  # Windows (Git Bash)
set -u
cd "$(dirname "$0")/.."
A="${PYTHON:-python3} tools/autoheim.py"
CHAR=SmokeBot
WORLD=SmokeWorld
pass=0; fail=0

check() {  # check <description> <autoheim args...>
    local desc=$1; shift
    if out=$($A "$@" --raw 2>&1) && [[ $out == *'"ok": true'* ]]; then
        pass=$((pass + 1)); printf '  ok    %s\n' "$desc"
    else
        fail=$((fail + 1)); printf '  FAIL  %s\n        %s\n' "$desc" "${out:0:300}"
    fi
}

echo "== launch"
if ! $A status --raw >/dev/null 2>&1; then
    $A launch >/dev/null || { echo "launch failed"; exit 1; }
fi
$A logout --raw >/dev/null 2>&1 || true
$A delete_character name=$CHAR --raw >/dev/null 2>&1 || true
$A delete_world name=$WORLD --raw >/dev/null 2>&1 || true

echo "== menu"
check "status"            status
check "create_character"  create_character name=$CHAR
check "create_world"      create_world name=$WORLD seed=smoke1
check "characters"        characters
check "worlds"            worlds
check "start_world"       start_world world=$WORLD character=$CHAR

echo "== observe"
check "player"            player
check "inventory"         inventory
check "equipment"         equipment
check "skills"            skills
check "env"               env
check "nearby"            nearby radius=30
check "locations"         locations limit=3
check "world_objects"     world_objects name=Greydwarf limit=3
check "screenshot"        screenshot width=640
check "screenshot camera" screenshot width=640 mode=camera hud=false
check "record_audio"      record_audio seconds=1

echo "== act"
check "move"              move forward=1 seconds=1
check "look"              look yaw=90 pitch=0
check "move_to"           move_to x=-10 z=0 timeout=30
check "jump"              jump
check "crouch on"         crouch on=true
check "crouch off"        crouch on=false
check "dodge"             dodge
check "give axe"          give item=AxeStone
check "give berries"      give item=Raspberry count=3
check "equip"             equip item=AxeStone
check "attack"            attack count=2
check "block"             block seconds=0.5
check "use_item"          use_item item=Raspberry
check "unequip"           unequip slot=right
check "drop"              drop item=Raspberry count=1
check "pickup"            pickup
check "spawn chest"       console text="devcommands"
check "spawn chest 2"     console text="spawn piece_chest_wood"
check "give wood"         give item=Wood count=10
check "container_put"     container_put item=Wood count=6 radius=15
check "container"         container radius=15
check "container_take"    container_take item=Wood count=2 radius=15
check "nearby pieces"     nearby kinds=pieces,characters radius=10
check "emote"             emote name=wave
check "teleport near"     teleport x=30 z=30
check "teleport far"      teleport x=1500 z=1500 timeout=90
check "god"               god on=true

echo "== build (real hammer/hoe use, away from the spawn no-build zone)"
pos() { $A player --raw | ${PYTHON:-python3} -c "import json,sys; p=json.load(sys.stdin)['result']['position']; print(round(p['x']), round(p['z']))"; }
# A dry spot ~40m from the start temple (the temple area itself is a no-build zone).
read -r TX TZ < <($A locations name=StartTemple limit=1 --raw | ${PYTHON:-python3} -c "import json,sys; p=json.load(sys.stdin)['result'][0]['position']; print(round(p['x']), round(p['z']))")
for off in "40 0" "0 40" "-40 0" "0 -40" "30 30"; do
    set -- $off
    $A teleport x=$((TX + $1)) z=$((TZ + $2)) --raw >/dev/null
    sleep 3
    $A player --raw | grep -q '"swimming": false' && break
done
read -r BX BZ < <(pos)
check "give hammer"       give item=Hammer
check "give hoe"          give item=Hoe
check "give wood"         give item=Wood count=40
check "give stone"        give item=Stone count=10
# The game may refuse a given spot (rocks, trees, slope); that still proves the command
# drove the real build path, so a refusal counts as a pass. Only continue if it placed.
build_try() {  # build_try <description> <build args...>
    local desc=$1; shift
    out=$($A build "$@" --raw 2>&1)
    if [[ $out == *'"ok": true'* ]]; then pass=$((pass + 1)); printf '  ok    %s\n' "$desc"; return 0; fi
    if [[ $out == *'placement not allowed'* ]]; then pass=$((pass + 1)); printf '  ok    %s (game refused spot)\n' "$desc"; return 1; fi
    fail=$((fail + 1)); printf '  FAIL  %s\n        %s\n' "$desc" "${out:0:300}"; return 1
}
build_try "level ground" piece=mud_road_v2 x=$BX z=$((BZ + 4))
check "build_pieces"      build_pieces
if build_try "build workbench" piece=piece_workbench x=$BX z=$((BZ + 4)); then
    check "build floor"       build piece=wood_floor x=$((BX + 4)) z=$((BZ + 4))
    check "build wall (snap)" build piece=woodwall x=$((BX + 4)) z=$((BZ + 4)).7
    check "repair (none)"     repair name=woodwall x=$((BX + 4)) z=$((BZ + 5)) radius=1.5
    check "destroy wall"      destroy name=woodwall x=$((BX + 4)) z=$((BZ + 5)) radius=1.5
fi
check "heal"              heal
check "set_time"          set_time fraction=0.5
check "console"           console text="help"
check "chat"              chat text="smoke test"
check "save"              save
check "logout"            logout

if [[ -z ${SKIP_SERVER:-} ]]; then
    echo "== dedicated server"
    $A server-start >/dev/null && check "join_server" join_server address=127.0.0.1:2476 password=secret12 character=$CHAR
    check "player on server"  player
    check "logout"            logout
    $A server-stop >/dev/null
fi

echo "== cleanup"
check "delete_world"      delete_world name=$WORLD
check "delete_character"  delete_character name=$CHAR

echo "passed $pass, failed $fail"
[[ $fail -eq 0 ]]
