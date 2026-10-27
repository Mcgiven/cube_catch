// DemoCamera.cs —— 演示用的自由相机。
//
//   右键按住拖动：转头（左右偏航、上下俯仰）
//   滚轮：前后推进 / 后退
//   方向键：平移（上下左右）
//   PageUp / PageDown：升高 / 降低
//
// 为什么用方向键而不是 WASD：WASD 是智能体的手动控制键（HeuristicOnly 的一方会响应），
// 用它们会同时把方块开走（2026-10-27）。
using UnityEngine;

public class DemoCamera : MonoBehaviour
{
    public float lookSpeed = 3.0f;
    public float panSpeed = 12.0f;
    public float zoomSpeed = 15.0f;
    public float minPitch = -20f;
    public float maxPitch = 85f;

    float m_Yaw;
    float m_Pitch;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        var cam = Camera.main;
        if (cam == null) return;
        if (cam.GetComponent<DemoCamera>() != null) return;
        cam.gameObject.AddComponent<DemoCamera>();
    }

    void Start()
    {
        var e = transform.eulerAngles;
        m_Yaw = e.y;
        m_Pitch = e.x > 180f ? e.x - 360f : e.x;
    }

    void Update()
    {
        // 转头：按住右键拖动
        if (Input.GetMouseButton(1))
        {
            m_Yaw += Input.GetAxis("Mouse X") * lookSpeed;
            m_Pitch -= Input.GetAxis("Mouse Y") * lookSpeed;
            m_Pitch = Mathf.Clamp(m_Pitch, minPitch, maxPitch);
            transform.rotation = Quaternion.Euler(m_Pitch, m_Yaw, 0f);
        }

        // 缩放：滚轮沿视线前后
        float scroll = Input.GetAxis("Mouse ScrollWheel");
        if (Mathf.Abs(scroll) > 0.0001f)
        {
            transform.position += transform.forward * (scroll * zoomSpeed);
        }

        // 平移：方向键
        Vector3 move = Vector3.zero;
        if (Input.GetKey(KeyCode.LeftArrow)) move -= transform.right;
        if (Input.GetKey(KeyCode.RightArrow)) move += transform.right;
        if (Input.GetKey(KeyCode.UpArrow)) move += Vector3.Cross(transform.right, Vector3.up).normalized;
        if (Input.GetKey(KeyCode.DownArrow)) move -= Vector3.Cross(transform.right, Vector3.up).normalized;
        if (Input.GetKey(KeyCode.PageUp)) move += Vector3.up;
        if (Input.GetKey(KeyCode.PageDown)) move -= Vector3.up;

        if (move != Vector3.zero)
        {
            float boost = Input.GetKey(KeyCode.LeftShift) ? 3f : 1f;
            transform.position += move.normalized * (panSpeed * boost * Time.deltaTime);
        }
    }
}
