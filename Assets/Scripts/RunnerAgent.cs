using UnityEngine;
using Unity.MLAgents;
using Unity.MLAgents.Sensors;
using Unity.MLAgents.Actuators;

public class RunnerAgent : Agent
{
    public Transform chaser;
    public float moveSpeed = 3f;
    private Rigidbody rBody;
    private int stepCount = 0;

    public override void Initialize()
    {
        rBody = GetComponent<Rigidbody>();
        // 开启连续碰撞检测，防止高速穿透
        rBody.collisionDetectionMode = CollisionDetectionMode.Continuous;
    }

    public override void OnEpisodeBegin()
    {
        stepCount = 0;

        // 强制拉开至少 2.5 米的距离，最多尝试 100 次防止死循环
        int attempts = 0;
        do
        {
            transform.localPosition = new Vector3(Random.Range(-4f, 4f), 0.5f, Random.Range(-4f, 4f));
            chaser.localPosition = new Vector3(Random.Range(-4f, 4f), 0.5f, Random.Range(-4f, 4f));
            attempts++;
        } while (Vector3.Distance(transform.localPosition, chaser.localPosition) < 2.5f && attempts < 100);

        rBody.velocity = Vector3.zero;
        rBody.angularVelocity = Vector3.zero;
    }

    public override void CollectObservations(VectorSensor sensor)
    {
        sensor.AddObservation(transform.localPosition);
        sensor.AddObservation(chaser.localPosition);
        sensor.AddObservation(rBody.velocity);
    }

    public override void OnActionReceived(ActionBuffers actions)
    {
        // 1. 存活奖励（核心：只要活着，每步都有奖励）
        // 1000步 * 0.01 = 10分。活下来就是硬道理！
        AddReward(0.01f);

        // 2. 超时存活奖励（活满1000步，额外给5分）
        stepCount++;
        if (stepCount >= 1000)
        {
            AddReward(5.0f);
            EndEpisode();
            return;
        }

        // 3. 执行动作
        float moveX = actions.ContinuousActions[0];
        float moveZ = actions.ContinuousActions[1];
        rBody.velocity = new Vector3(moveX * moveSpeed, rBody.velocity.y, moveZ * moveSpeed);

        // 4. 危险距离惩罚（代替之前的距离奖励）
        // 距离小于 1.5 米时扣分，逼迫它逃离危险区域
        float distanceToChaser = Vector3.Distance(transform.localPosition, chaser.localPosition);
        if (distanceToChaser < 1.5f)
        {
            AddReward(-0.02f);
        }

        // 5. 掉出地图惩罚（安全第一）
        if (transform.localPosition.y < 0)
        {
            AddReward(-2.0f);
            EndEpisode();
        }
    }

    // 物理碰撞判定（被抓到直接重罚）
    private void OnCollisionEnter(Collision collision)
    {
        if (collision.transform == chaser)
        {
            AddReward(-2.0f);
            EndEpisode();
        }
    }

    public override void Heuristic(in ActionBuffers actionsOut)
    {
        var continuousActionsOut = actionsOut.ContinuousActions;
        float moveX = 0f;
        float moveZ = 0f;

        if (Input.GetKey(KeyCode.W) || Input.GetKey(KeyCode.UpArrow)) moveZ = 1f;
        if (Input.GetKey(KeyCode.S) || Input.GetKey(KeyCode.DownArrow)) moveZ = -1f;
        if (Input.GetKey(KeyCode.A) || Input.GetKey(KeyCode.LeftArrow)) moveX = -1f;
        if (Input.GetKey(KeyCode.D) || Input.GetKey(KeyCode.RightArrow)) moveX = 1f;

        continuousActionsOut[0] = moveX;
        continuousActionsOut[1] = moveZ;
    }
}