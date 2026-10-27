using UnityEngine;
using Unity.MLAgents;
using Unity.MLAgents.Sensors;
using Unity.MLAgents.Actuators;

public class CatcherAgent : Agent
{
    public Transform target;
    public float moveSpeed = 3f;
    private Rigidbody rBody;
    private int stepCount = 0;

    public override void Initialize()
    {
        rBody = GetComponent<Rigidbody>();
        // 关键：防止物理穿透
        rBody.collisionDetectionMode = CollisionDetectionMode.Continuous;
    }

    public override void OnEpisodeBegin()
    {
        stepCount = 0;
        // 强制拉开至少 3 米，最多尝试 100 次防止死循环
        int attempts = 0;
        do
        {
            transform.localPosition = new Vector3(Random.Range(-4f, 4f), 0.5f, Random.Range(-4f, 4f));
            target.localPosition = new Vector3(Random.Range(-4f, 4f), 0.5f, Random.Range(-4f, 4f));
            attempts++;
        } while (Vector3.Distance(transform.localPosition, target.localPosition) < 3f && attempts < 100);

        rBody.velocity = Vector3.zero;
        rBody.angularVelocity = Vector3.zero;
    }

    public override void CollectObservations(VectorSensor sensor)
    {
        sensor.AddObservation(transform.localPosition);
        sensor.AddObservation(target.localPosition);
        sensor.AddObservation(rBody.velocity);
    }

    public override void OnActionReceived(ActionBuffers actions)
    {
        stepCount++;
        if (stepCount >= 1000)
        {
            AddReward(-0.5f);
            EndEpisode();
            return;
        }

        float moveX = actions.ContinuousActions[0];
        float moveZ = actions.ContinuousActions[1];
        rBody.velocity = new Vector3(moveX * moveSpeed, rBody.velocity.y, moveZ * moveSpeed);

        AddReward(-0.0005f);

        if (transform.localPosition.y < 0)
        {
            AddReward(-1.0f);
            EndEpisode();
        }
    }

    // 物理碰撞判定（绝对真实，不会作弊）
    private void OnCollisionEnter(Collision collision)
    {
        if (collision.transform == target)
        {
            AddReward(2.0f);
            EndEpisode();
        }
    }

    public override void Heuristic(in ActionBuffers actionsOut)
    {
        var continuousActionsOut = actionsOut.ContinuousActions;
        float moveX = 0f, moveZ = 0f;
        if (Input.GetKey(KeyCode.W) || Input.GetKey(KeyCode.UpArrow)) moveZ = 1f;
        if (Input.GetKey(KeyCode.S) || Input.GetKey(KeyCode.DownArrow)) moveZ = -1f;
        if (Input.GetKey(KeyCode.A) || Input.GetKey(KeyCode.LeftArrow)) moveX = -1f;
        if (Input.GetKey(KeyCode.D) || Input.GetKey(KeyCode.RightArrow)) moveX = 1f;
        continuousActionsOut[0] = moveX;
        continuousActionsOut[1] = moveZ;
    }
}