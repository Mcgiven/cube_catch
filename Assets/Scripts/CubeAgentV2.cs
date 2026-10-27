// CubeAgentV2.cs —— 六面立方体、有限视野、底部推力的 v2 智能体。
//
// 一个类覆盖两个角色（Catcher / Runner），用 Inspector 里的 isCatcher 区分。
// 与 v1 的区别：
//   1) 观测 9 → 23 维：加了朝向、角速度、是否着地、对手是否可见
//   2) 动作 2 → 4 维：前后、左右、偏航、起跳（只有底部着地时才有效）
//   3) 视野：只有正前方 100° 锥体内、且射线不被遮挡时才看得见对手
//   4) 速度不对称：前进 > 左右 > 后退
//
// 注意：观测维度变了，v1 训出来的 .onnx/.pt 全部作废，v2 必须从零训练。
using UnityEngine;
using Unity.MLAgents;
using Unity.MLAgents.Sensors;
using Unity.MLAgents.Actuators;

public class CubeAgentV2 : Agent
{
    [Header("角色")]
    public bool isCatcher = true;
    public Transform opponent;

    [Header("运动参数")]
    public float forwardSpeed = 4.0f;    // 前进（最快）
    public float sideSpeed = 2.5f;       // 左右（次之）
    public float backwardSpeed = 1.5f;   // 后退（最慢）
    public float thrustAccel = 25.0f;    // 推力加速度（必须大于静摩擦换算的 ~6 m/s²，否则推不动）

    [Header("推力作用点")]
    [Tooltip("勾上 = 力作用在底面（底部推进器，会把自己往前掀）；不勾 = 作用在质心（更稳，翻转只来自地面摩擦）")]
    public bool applyForceAtBottom = false;
    [Tooltip("力作用点离底面的高度。0 = 贴着底面，最容易翻；0.5 = 质心，不会因推力而翻")]
    [Range(0f, 0.5f)] public float thrustHeight = 0.1f;
    public float maxYawRate = 2.5f;    // 偏航角速度上限（弧度/秒），约 143°/s
    public float yawAccel = 30.0f;     // 偏航角加速度（rad/s²）。低于 12 会被底面摩擦吃掉，转不动
    public float maxRollRate = 3.5f;   // 翻滚角速度上限，翻回来大约需要 0.45 秒
    public float rollAccel = 60.0f;    // 翻滚角加速度（rad/s²）；掀翻需要约 29 rad/s²，翻回来要更大
    public float jumpImpulse = 6.0f;   // 顶点 = v²/(2g) = 1.83 米，才够跳上 1 米高的基座（4.5 只有 1.03 米，贴着边）

    [Header("视野")]
    [Range(10f, 90f)] public float halfFovDeg = 50f;   // 半角，50 = 全角 100°
    public float viewRange = 15f;
    public bool useOcclusion = false;                  // 先关掉遮挡判定，确认能学会再打开
    public LayerMask occluderMask = ~0;                // 默认全部，可在 Inspector 里只勾墙体

    [Header("基座（让对手站在高处，抓人者必须跳上去才够得到）")]
    public bool runnerOnPedestal = true;
    public float pedestalHeight = 0.5f;   // 课程第一步：0.5 米（仍必须跳，但窗口宽）
    public float pedestalWidth = 4f;    // 台面宽度：3 米时跳跃落地容错太低，卡在 +0.92；加到 4 米
    GameObject pedestal;

    [Header("外观")]
    public bool transparentWalls = true;              // 围墙半透明，方便看清场内
    [Range(0.05f, 0.9f)] public float wallAlpha = 0.25f;
    public bool showFacing = true;                    // 正前方贴他画的图（Resources/Textures/front_*.png）
    // 颜色不再走序列化字段：字段的旧值会被 Unity 存在组件里，改了默认值也不生效（2026-10-26 踩过）。
    // 需要改颜色就直接改这里的常量。
    static readonly Color CatcherBody = Color.black;   // 抓捕者：除正面外全黑
    static readonly Color RunnerBody = Color.white;    // 逃跑者：除正面外全白

    [Header("地面")]
    public LayerMask groundMask = ~0;
    public float groundCheckDistance = 0.65f;

    [Header("回合")]
    public int maxSteps = 3000;

    const int ObsSize = 23;

    Rigidbody rb;
    int step;

    public override void Initialize()
    {
        rb = GetComponent<Rigidbody>();
        rb.interpolation = RigidbodyInterpolation.Interpolate;
        rb.collisionDetectionMode = CollisionDetectionMode.Continuous;
        // 让方块有"黏地感"，否则直接加法向力会一直打滑
        rb.drag = 0.5f;
        rb.angularDrag = 1f;   // 原来是 5：角阻尼太大，10° 的侧倾会被压到 ~1 rad/s，看起来像"黏住慢慢倒"
        rb.constraints = RigidbodyConstraints.None;

        if (isCatcher && transparentWalls) MakeWallsTransparent();
        if (showFacing) AddFrontDecal();

        // 基座：一块和方块同尺寸的静态立方体，对手站在它上面。
        // 必须挂在场地根节点下并用 localPosition——12 个场地沿 x 轴排开，用世界坐标会全部叠在原点。
        if (isCatcher && runnerOnPedestal)
        {
            pedestal = GameObject.CreatePrimitive(PrimitiveType.Cube);
            pedestal.name = "Pedestal";
            pedestal.transform.SetParent(transform.parent, false);
            pedestal.transform.localScale = new Vector3(pedestalWidth, pedestalHeight, pedestalWidth);
            pedestal.transform.localPosition = new Vector3(0f, pedestalHeight * 0.5f, 0f);
            var prb = pedestal.GetComponent<Rigidbody>();
            if (prb != null) Destroy(prb);
        }
    }

    public override void OnEpisodeBegin()
    {
        step = 0;
        seeCount = 0;

        // 位置一律由 Catcher 决定；Runner 自己不清零位置，否则两边的 OnEpisodeBegin 会互相覆盖。
        rb.velocity = Vector3.zero;
        rb.angularVelocity = Vector3.zero;
        if (!isCatcher) return;

        // 基座位置（每回合随机换地方）：抓人者必须跳上去才能碰到高台上的对手
        float px = Random.Range(-6.5f, 6.5f);
        float pz = Random.Range(-6.5f, 6.5f);
        if (pedestal != null)
        {
            pedestal.transform.localPosition = new Vector3(px, pedestalHeight * 0.5f, pz);
        }

        Vector3 targetPos = new Vector3(px, pedestalHeight + 0.5f, pz);

        Vector3 a;
        int guard = 0;
        do
        {
            a = new Vector3(Random.Range(-8f, 8f), 0.6f, Random.Range(-8f, 8f));
            guard++;
        } while (Vector3.Distance(a, targetPos) < 4f && guard < 100);

        transform.localPosition = a;
        opponent.localPosition = targetPos;

        // 诊断：回合开始也打一行。如果这条频繁出现而 [EP] TIMEOUT 从不出现，
        // 说明是环境被外部重置（不是我们的结束条件触发）。
        Debug.Log($"[EPB] {name} begin pedestal={px:F1},{pz:F1}");

        rb.velocity = Vector3.zero;
        rb.angularVelocity = Vector3.zero;
        transform.rotation = Quaternion.Euler(0f, Random.Range(0f, 360f), 0f);

        // 距离塑形用的基准，必须在每回合开始时重置，否则第一帧会拿到一次巨大的假奖励
        lastDist = Vector3.Distance(transform.position, opponent.position);
        lastDy = Mathf.Abs(opponent.position.y - transform.position.y);
    }

    // 把这一整个场地里的四面墙换成半透明材质（只影响渲染，不影响碰撞）。
    // 墙是 Arena 根节点下名字以 "wall" 开头的子物体。
    void MakeWallsTransparent()
    {
        var shader = Shader.Find("Standard");
        if (shader == null)
        {
            Debug.LogWarning("[CubeAgentV2] 找不到 Standard 着色器，围墙保持不透明");
            return;
        }

        var mat = new Material(shader);
        mat.SetFloat("_Mode", 3f);                                   // 3 = Transparent
        mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
        mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        mat.SetInt("_ZWrite", 0);
        mat.DisableKeyword("_ALPHATEST_ON");
        mat.EnableKeyword("_ALPHABLEND_ON");
        mat.DisableKeyword("_ALPHAPREMULTIPLY_ON");
        mat.renderQueue = 3000;
        mat.color = new Color(1f, 1f, 1f, wallAlpha);

        foreach (Transform child in transform.parent)
        {
            if (!child.name.StartsWith("wall")) continue;
            var r = child.GetComponent<Renderer>();
            if (r != null) r.sharedMaterial = mat;
        }
    }

    // 正前方（transform.forward，也就是 +Z）贴一张自定义贴图。
    // 贴图由他本人绘制，放在 Assets/Resources/Textures/front.png，运行时读取——
    // 换图只需要替换这个文件并重新打包，不用改代码。
    // 用一块很薄的方块当"贴纸"，不用 Quad：Quad 的正面朝向在 Unity 里容易搞反。
    // 贴纸不带碰撞体，否则会改变物理行为（蹭到对手就算抓到）。
    void AddFrontDecal()
    {
        // 先找角色专用图（front_catcher / front_runner），没有再退回通用图（front）
        var tex = Resources.Load<Texture2D>(isCatcher ? "Textures/front_catcher" : "Textures/front_runner");
        if (tex == null) tex = Resources.Load<Texture2D>("Textures/front");
        if (tex == null)
        {
            Debug.LogWarning("[CubeAgentV2] 没找到 Assets/Resources/Textures/front.png，正面保持空白");
            return;
        }

        if (isCatcher)
        {
            var body = GetComponent<Renderer>();
            if (body != null)
            {
                var bodyMat = new Material(body.sharedMaterial);
                bodyMat.color = CatcherBody;
                body.sharedMaterial = bodyMat;
            }
        }
        else
        {
            var body2 = GetComponent<Renderer>();
            if (body2 != null)
            {
                var bodyMat2 = new Material(body2.sharedMaterial);
                bodyMat2.color = RunnerBody;
                body2.sharedMaterial = bodyMat2;
            }
        }

        var decal = GameObject.CreatePrimitive(PrimitiveType.Cube);
        decal.name = "FrontDecal";
        var col = decal.GetComponent<Collider>();
        if (col != null) Destroy(col);

        decal.transform.SetParent(transform, false);
        decal.transform.localScale = new Vector3(0.9f, 0.9f, 0.02f);
        decal.transform.localPosition = new Vector3(0f, 0f, 0.5f + 0.01f);   // 贴在 +Z 面外侧

        var shader = Shader.Find("Unlit/Transparent");
        if (shader == null) shader = Shader.Find("Standard");
        var mat = new Material(shader) { mainTexture = tex };
        mat.color = Color.white;
        decal.GetComponent<Renderer>().sharedMaterial = mat;
    }

    // ---------- 观测 ----------
    bool Grounded(out RaycastHit hit)
    {
        return Physics.Raycast(transform.position, Vector3.down, out hit,
                               groundCheckDistance, groundMask, QueryTriggerInteraction.Ignore);
    }

    /// 对手是否在正前方视野锥内、且没有被遮挡
    bool SeesOpponent()
    {
        Vector3 to = opponent.position - transform.position;
        float dist = to.magnitude;
        if (dist > viewRange) return false;
        if (Vector3.Angle(transform.forward, to) > halfFovDeg) return false;

        // 2026-10-26 简化：先不做遮挡判定。
        // 原因：射线从自身中心射出，若打到自身碰撞体或地板边沿，会把"看得见"判成"看不见"，
        // 导致观测里的对手信息长期为 0，策略学不到任何东西。先确认能学，再把遮挡加回来。
        if (!useOcclusion) return true;

        if (Physics.Raycast(transform.position, to.normalized, out RaycastHit hit,
                            dist + 0.1f, occluderMask, QueryTriggerInteraction.Ignore))
        {
            return hit.transform == opponent || hit.transform.IsChildOf(opponent);
        }
        return false;
    }

    public override void CollectObservations(VectorSensor sensor)
    {
        // 1) 自身
        sensor.AddObservation(transform.localPosition);        // 3
        sensor.AddObservation(rb.velocity);                    // 3
        sensor.AddObservation(transform.up);                   // 3  朝向（含翻转）
        sensor.AddObservation(transform.forward);              // 3
        sensor.AddObservation(rb.angularVelocity);             // 3

        bool grounded = Grounded(out _);
        // 只有底部朝下才算"能发力"
        bool bottomDown = Vector3.Dot(transform.up, Vector3.up) > 0.6f;
        sensor.AddObservation(grounded && bottomDown ? 1f : 0f); // 1

        // 2) 对手（看不见时全 0）
        bool sees = SeesOpponent();
        if (sees) seeCount++;
        sensor.AddObservation(sees ? 1f : 0f);                  // 1
        if (sees)
        {
            Vector3 rel = opponent.position - transform.position;
            sensor.AddObservation(transform.InverseTransformDirection(rel)); // 3（机体坐标）
            sensor.AddObservation(opponent.GetComponent<Rigidbody>().velocity); // 3
        }
        else
        {
            sensor.AddObservation(Vector3.zero);
            sensor.AddObservation(Vector3.zero);
        }
        // 合计 23
    }

    // ---------- 动作 ----------

    public override void OnActionReceived(ActionBuffers actions)
    {
        step++;

        float fwd = actions.ContinuousActions[0];    // -1..1
        float side = actions.ContinuousActions[1];   // -1..1
        float yaw = actions.ContinuousActions[2];    // -1..1
        float jump = actions.ContinuousActions[3];   // 0..1  跳跃
        float roll = actions.ContinuousActions[4];   // -1..1 翻滚（翻倒后自救）

        bool grounded = Grounded(out _);
        bool bottomDown = Vector3.Dot(transform.up, Vector3.up) > 0.6f;
        bool canThrust = grounded && bottomDown;

        // 平动输入的"闭包"：同时按 W+D 时，两个分量的合成不能超过 1，
        // 否则对角线的实际速度会大于纯前进（4² + 2.5² 开根 = 4.7 > 4）。
        Vector2 input = new Vector2(fwd, side);
        if (input.sqrMagnitude > 1f) input.Normalize();
        fwd = input.x;
        side = input.y;

        // 转动指令先存起来，真正的扭矩在 FixedUpdate 里每个物理步施加。
        // 原因：Decision Period = 5，如果只在决策时给一次扭矩，每 5 个物理步才推一下，
        // 立刻被底面摩擦吃掉——表现为"只有起跳离地那一下看得出在转"（2026-10-26 实测）。
        cmdYaw = yaw;
        cmdRoll = roll;

        if (canThrust)
        {
            // 靠底部摩擦力推：施加真实的加速度力，让物理引擎自己决定滑不滑、滑多快。
            // 关键在量级：4 m/s² 只有 4 N，跨不过约 5.9 N 的静摩擦；
            // 现在用 thrustAccel = 25 m/s²，静摩擦一动就破，之后靠速度上限约束最高速。
            Vector3 dir = transform.forward * fwd + transform.right * side;
            if (dir.sqrMagnitude > 1f) dir.Normalize();

            Vector3 force = dir * thrustAccel;
            if (applyForceAtBottom)
            {
                // 底部推进器：力作用在底面中心，会产生一个把它往前掀的力矩。
                // 这是"只有底部提供动力"这句话的物理含义——推力在质心下方，所以推动本身就会抬后角。
                // AddForceAtPosition 不支持 Acceleration 模式，要自己乘质量。
                Vector3 bottom = transform.position
                                 - transform.up * (transform.localScale.y * 0.5f)
                                 + transform.up * thrustHeight;
                rb.AddForceAtPosition(force * rb.mass, bottom, ForceMode.Force);
            }
            else
            {
                // 质心施力：力学上更"稳"，翻转只来自地面摩擦，不来自推力本身。
                rb.AddForce(force, ForceMode.Acceleration);
            }

            // 分轴限速：前进/后退各一个上限，侧移一个上限（不做这个会一路加速到几十米每秒）
            Vector3 v = rb.velocity;
            Vector3 f = transform.forward;
            Vector3 r = transform.right;
            Vector3 horiz = new Vector3(v.x, 0f, v.z);
            float vF = Vector3.Dot(horiz, f);
            float vS = Vector3.Dot(horiz, r);
            float capF = Mathf.Abs(fwd) * (fwd >= 0f ? forwardSpeed : backwardSpeed);
            float capS = Mathf.Abs(side) * sideSpeed;
            vF = Mathf.Clamp(vF, -capF, capF);
            vS = Mathf.Clamp(vS, -capS, capS);
            Vector3 capped = f * vF + r * vS;
            rb.velocity = new Vector3(capped.x, v.y, capped.z);

            if (jump > 0.5f)
            {
                // 跳跃力度随动作值缩放：0.5 是起跳门槛，1.0 是全力。
                // 这样它既能跳上高台，也能收着力气小跳（2026-10-27）。
                float strength = Mathf.InverseLerp(0.5f, 1f, Mathf.Clamp01(jump));
                rb.AddForce(Vector3.up * (jumpImpulse * Mathf.Max(0.25f, strength)),
                            ForceMode.VelocityChange);
            }
        }

        // 奖励
        if (isCatcher)
        {
            AddReward(-0.0005f);
            float dist = Vector3.Distance(transform.position, opponent.position);
            // 距离塑形：靠近给正，远离给负。0.05/米 —— 横穿场地约 +1.0，接近抓捕奖励的一半
            AddReward((lastDist - dist) * 0.05f);
            lastDist = dist;

            // 高度塑形（2026-10-26 加）：只有三维距离时，走到台子脚下塑形就饱和了，
            // 再往上跳最多只能多拿 1 米的奖励，探索不出起跳时机。单独奖励"爬高"这一步。
            float dy = Mathf.Abs(opponent.position.y - transform.position.y);
            AddReward((lastDy - dy) * 0.5f);
            lastDy = dy;
        }
        else
        {
            AddReward(0.01f);
            float dist = Vector3.Distance(transform.position, opponent.position);
            if (dist < 1.5f) AddReward(-0.02f);
        }

        // 出界 / 超时
        if (transform.localPosition.y < -1f)
        {
            Debug.Log($"[EP] {name} FELL step={step} dist={Vector3.Distance(transform.position, opponent.position):F1} see={seeCount}");
            AddReward(isCatcher ? -1.0f : -2.0f);
            EndEpisode();
            return;
        }
        if (step >= maxSteps)
        {
            Debug.Log($"[EP] {name} TIMEOUT step={step} dist={Vector3.Distance(transform.position, opponent.position):F1} see={seeCount}");
            AddReward(isCatcher ? -0.5f : 5.0f);
            EndEpisode();
        }
    }

    float lastDist;
    float lastDy;   // 高度塑形用的上一帧垂直差
    int seeCount;   // 本回合"看见对手"的决策次数，用来验证视野到底有没有生效

    private void OnCollisionEnter(Collision collision)
    {
        if (collision.transform != opponent) return;

        // 每局结束时打一行，用来核对"到底有没有撞上"（Player.log 里搜 [EP]）
        Debug.Log($"[EP] {name} {(isCatcher ? "CAUGHT" : "GOT_CAUGHT")} step={step} see={seeCount}");

        if (isCatcher)
        {
            AddReward(2.0f);
            EndEpisode();
        }
        else
        {
            AddReward(-2.0f);
            EndEpisode();
        }
    }

    float cmdYaw, cmdRoll;

    // 每个物理步施加一次转动扭矩。放在 FixedUpdate 而不是 OnActionReceived，
    // 是因为决策周期是 5 个物理步：只在决策时给一次扭矩，会被接触摩擦当场吃掉。
    void FixedUpdate()
    {
        if (rb == null) return;

        bool grounded = Grounded(out _);
        bool bottomDown = Vector3.Dot(transform.up, Vector3.up) > 0.6f;
        bool onGround = grounded && bottomDown;

        // 扭转：绕"机体自己的 up 轴"，且只要接地就能扭。
        //   站着时 up 是竖直的 → 就是转向（原来的偏航）
        //   侧躺时 up 是水平的 → 扭一下会把自己翻回正面，等于自救
        // 这样侧翻不需要额外动作，Q/E 一个动作同时解决转向和回正（2026-10-27 他的方案）。
        // 为什么直接设角速度：方块是整面接触，扭矩会被接触解算器每步抹掉（10-26 实测）。
        if (grounded)
        {
            Vector3 axis = transform.up;
            Vector3 w = rb.angularVelocity;
            float cur = Vector3.Dot(w, axis);
            float wish = cmdYaw * maxYawRate;
            rb.angularVelocity = w + axis * (wish - cur);
        }

        // 前滚翻：绕机体右轴，恢复成最初的行为。
        // 曾经改成"回正轴"，结果方块学会了用棱立起来（2026-10-27 演示中看到），已撤销。
        // 回正现在交给 Q/E（绕机体 up 轴扭）。
        {
            Vector3 axis = transform.right;
            float cur = Vector3.Dot(rb.angularVelocity, axis);
            float wish = cmdRoll * maxRollRate;
            float diff = wish - cur;
            if (Mathf.Abs(diff) > 0.05f)
                rb.AddTorque(axis * Mathf.Sign(diff) * rollAccel, ForceMode.Acceleration);
        }
    }

    // ---------- 手动控制（调试用） ----------

    public override void Heuristic(in ActionBuffers actionsOut)
    {
        var a = actionsOut.ContinuousActions;
        // 必须按下标判断长度：动作数会被预制体改（现在是 3），写超界会抛 IndexOutOfRange，
        // 而且异常会让动作写不进去——表现为"键盘完全没反应"（2026-10-26 实测 367 次异常）。
        if (a.Length > 0) a[0] = Input.GetKey(KeyCode.W) ? 1f : (Input.GetKey(KeyCode.S) ? -1f : 0f);
        if (a.Length > 1) a[1] = Input.GetKey(KeyCode.D) ? 1f : (Input.GetKey(KeyCode.A) ? -1f : 0f);
        if (a.Length > 2) a[2] = Input.GetKey(KeyCode.E) ? 1f : (Input.GetKey(KeyCode.Q) ? -1f : 0f);
        if (a.Length > 3)
        {
            // 手动控制改成"蓄力跳"：按下时不跳，松手时按按压时长决定力度。
            // 短按 → 0.55（小跳），长按 → 1.0（大跳）。
            // 只影响 Heuristic；模型自己的跳跃力度仍是动作值的连续函数（见 OnActionReceived）。
            a[3] = 0f;
            if (m_JumpTicks > 0)
            {
                a[3] = m_JumpValue;
                m_JumpTicks--;
            }
        }
        if (a.Length > 4) a[4] = Input.GetKey(KeyCode.R) ? 1f : (Input.GetKey(KeyCode.F) ? -1f : 0f);
    }

    // 蓄力跳的状态（只在手动控制时使用）
    float m_SpaceHeld;
    float m_JumpValue;
    int m_JumpTicks;

    [Tooltip("按压超过这个秒数算大跳，否则小跳")]
    public float spaceHoldThreshold = 0.18f;

    void Update()
    {
        if (Input.GetKeyDown(KeyCode.Space)) m_SpaceHeld = 0f;
        if (Input.GetKey(KeyCode.Space)) m_SpaceHeld += Time.deltaTime;
        if (Input.GetKeyUp(KeyCode.Space))
        {
            // 0.55 对应约 25% 的力度（InverseLerp 后取 Max(0.25, ·)），1.0 是全力
            m_JumpValue = m_SpaceHeld > spaceHoldThreshold ? 1f : 0.55f;
            m_JumpTicks = 1;       // 只发一次，避免落地后不停连跳
            m_SpaceHeld = 0f;
        }
    }
}
