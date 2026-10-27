#!/bin/bash
# 连续训练 N 轮，每轮从上一轮的 checkpoint 热启动。
# 用法：run_rounds.sh <起始 checkpoint> <轮数>
set -u
cd "/f/MLprogram/CatcherGame" || exit 1

INIT="$1"
ROUNDS="$2"
START="${3:-1}"
BASE_CONFIG="config/CubeV2_jump9.yaml"

for k in $(seq 1 "$ROUNDS"); do
  i=$((START + k - 1))
  RUN="v2_r$i"
  CFG="config/round_$i.yaml"
  sed -e "s|init_path: .*|init_path: $INIT|" \
      -e "s|learning_rate: 3.0e-4|learning_rate: 3.0e-4|" \
      "$BASE_CONFIG" > "$CFG"

  echo "=== round $i start  init=$INIT  run=$RUN  $(date +%H:%M) ==="
  "F:/MLprogram/venv/Scripts/mlagents-learn.exe" "$CFG" \
      --env=Builds_V2/CatcherGame.exe --num-areas=12 --num-envs=4 \
      --time-scale=100 --no-graphics --run-id="$RUN" \
      --timeout-wait=300 --force > "/tmp/round_$i.log" 2>&1

  echo "--- round $i done ---"
  grep -E "Mean Reward" "/tmp/round_$i.log" | tail -1
  INIT="results/$RUN/Catcher/checkpoint.pt"
done
echo "ALL ROUNDS DONE"
