# Two Cubes, Trained Against Each Other

A 3D one-on-one pursuit game in Unity ML-Agents: a **Catcher** chases a **Runner** inside a walled
arena, and both are controlled by policies trained with PPO. Course project for *2026F Introduction
to Machine Learning* (Week 9 milestone).

**Team:** Xinye Chen · Zijin Wu · Kaicheng Xu · Miao He

---

## What is here

```
Assets/Scripts/CubeAgentV2.cs     the agent: observations, actions, rewards, physics
Assets/Scripts/ModelSwitcher.cs   runtime model picker (switch checkpoints without rebuilding)
Assets/Scripts/DemoCamera.cs      free camera for recording demos
Assets/Scripts/CatcherAgent.cs    v1 agent (kept for reference)
Assets/Prefabs/Arena_V2.prefab    the arena: floor, four walls, two agents, pedestal
Assets/Resources/Models/*.onnx    trained policies, selectable at runtime
Assets/Resources/Textures/*.png   front-face decals designed by hand
config/*.yaml                     ML-Agents training configurations
```

## Environment

| | v1 | v2 |
|---|---|---|
| Observation | 9 floats | **23 floats** (pose, velocity, orientation, angular velocity, grounded flag, opponent visibility and relative position) |
| Actions | 2 continuous | **5 continuous** (forward, strafe, yaw, jump, roll) |
| Vision | none (god view) | **180° cone, 30 m**, occlusion optional |
| Thrust | set velocity directly | force applied only while the bottom face touches the ground |
| Network | 2 × 128 | 3 × 256, three observation frames stacked (69 inputs) |

Rewards — Catcher: +2 on capture, −1 for leaving the arena, −0.5 on timeout, +0.05 per metre of
distance closed, plus a height term. Runner: +0.01 per step, −2 when caught, +5 for surviving.

## Results

**Throughput** — 12 arenas in one process, headless build:

| Configuration | Steps/s | 1 M steps |
|---|---|---|
| Editor, single arena | 861 | 19.4 min |
| Built + headless, 12 arenas | 1,747 | 9.5 min |
| 4 processes in parallel | 4,724 | 3.5 min |

CPU stays at 10–14 %: the bottleneck is the single-threaded Python sampling loop.

**v1** — Runner trained alone for 1.5 M steps: reward −1.43 → +4.88, survival 12 → 115 steps.
Six alternating rounds: the frozen Catcher improved 1.56 → 1.69 → 1.76 while the Runner's best
response plateaued near 1.1 — at equal speed in a closed arena the chaser is structurally favoured.

**v2** — against an immobile Runner, the Catcher reached **+2.24** (episode length 199 → 35).
With the target on a raised platform, distance shaping saturated at the foot of the platform; adding a
height term and lowering the platform started the jump, and nine further 1 M-step rounds took the mean
reward to **+2.33** (evaluation with `learning_rate: 0` gives **+2.29**, ≈ 94 % success per episode,
against a theoretical ceiling of about +2.5).

## Reproducing

```bash
# 1. Python side (ML-Agents 1.1.0, PyTorch 2.0.1)
pip install mlagents==1.1.0

# 2. Build the Unity scene (Assets/Scenes/multiple arena v2.unity) to Builds_V2/

# 3. Train
mlagents-learn config/CubeV2_jump9.yaml \
    --env=Builds_V2/CatcherGame.exe \
    --num-areas=12 --num-envs=4 --time-scale=100 --no-graphics \
    --run-id=v2_jump

# 4. Watch
tensorboard --logdir results
```

## Demos

Build and run the executable. The top-left panel lists every model in `Assets/Resources/Models`;
clicking one reloads the policy at runtime (`Agent.SetModel`), so checkpoints can be compared
without rebuilding. A second button hides all but one arena; the camera supports right-drag look,
scroll zoom and arrow-key panning.

## License

Course work; not licensed for redistribution.
