// ModelSwitcher.cs —— 运行时自由组合模型，不需要重新打包。
//
// 用法：
//   1. 把 .onnx 放进 Assets/Resources/Models/（文件名就是界面上的名字）
//   2. 打包一次
//   3. 运行 exe，左上角会出现两列按钮：左边给抓捕者选模型，右边给逃跑者选模型
//   4. 选完立刻生效，可以录不同组合的对比视频
//
// 原理：ML-Agents 的 Agent.SetModel() 会在运行时重建策略；
// 模型通过 Resources.LoadAll 读进来，所以换组合不需要重新构建 exe。
//
// 注意：模型的观测/动作维度必须和场景里 Behavior Parameters 的设置一致
//（当前是观测 23、堆叠 3、动作 5）。v1 的模型（观测 9 / 动作 2）不能用。
using System.Collections.Generic;
using Unity.MLAgents;
using Unity.MLAgents.Policies;
using Unity.Sentis;
using UnityEngine;

public class ModelSwitcher : MonoBehaviour
{
    static ModelSwitcher s_Instance;

    ModelAsset[] m_All;
    int m_CatcherIdx = 0;   // 0 = 不换模型
    int m_RunnerIdx = 0;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        if (s_Instance != null) return;
        var go = new GameObject("[ModelSwitcher]");
        Object.DontDestroyOnLoad(go);
        s_Instance = go.AddComponent<ModelSwitcher>();
    }

    void Awake()
    {
        m_All = Resources.LoadAll<ModelAsset>("Models");
        System.Array.Sort(m_All, (a, b) => string.Compare(a.name, b.name, System.StringComparison.Ordinal));
    }

    void Update()
    {
        // 快捷键：1~9 选抓捕者模型（0 = None），F1~F6 选逃跑者模型
        if (Input.GetKeyDown(KeyCode.Alpha0)) SetSelection(true, 0);
        for (int i = 0; i < 9 && i < m_All.Length; i++)
        {
            if (Input.GetKeyDown(KeyCode.Alpha1 + i)) SetSelection(true, i + 1);
        }
        for (int i = 0; i < 6 && i < m_All.Length; i++)
        {
            if (Input.GetKeyDown(KeyCode.F1 + i)) SetSelection(false, i + 1);
        }
    }

    void SetSelection(bool isCatcher, int idx)
    {
        if (isCatcher) m_CatcherIdx = idx; else m_RunnerIdx = idx;
        ApplyToScene(isCatcher, idx == 0 ? null : m_All[idx - 1]);
    }

    static void ApplyToScene(bool isCatcher, ModelAsset model)
    {
        if (model == null)
        {
            // 回到默认状态最稳的办法：重载场景（本组件挂了 DontDestroyOnLoad，不会消失）。
            // 直接 SetModel(null) 会让策略工厂拿到空模型，容易空引用。
            UnityEngine.SceneManagement.SceneManager.LoadScene(
                UnityEngine.SceneManagement.SceneManager.GetActiveScene().buildIndex);
            return;
        }

        var agents = Object.FindObjectsOfType<BehaviorParameters>();
        int n = 0;
        foreach (var bp in agents)
        {
            var cube = bp.GetComponent<CubeAgentV2>();
            if (cube == null || cube.isCatcher != isCatcher) continue;

            // 顺序很重要：必须先挂模型，再改 BehaviorType。
            // 反过来会抛 "Can't use Behavior Type InferenceOnly without a model"，
            // 因为 set_BehaviorType 内部会立刻重建策略（2026-10-26 实测）。
            bp.Model = model;
            bp.BehaviorType = BehaviorType.InferenceOnly;
            var a = bp.GetComponent<Agent>();
            if (a != null) a.SetModel(bp.BehaviorName, model);
            n++;
        }
        Debug.Log($"[ModelSwitcher] {(isCatcher ? "Catcher" : "Runner")} <- {model.name} applied to {n} agents");
    }

    void OnGUI()
    {
        if (m_All == null) return;
        const int W = 260;
        int rows = m_All.Length + 1;
        GUI.Box(new Rect(8, 8, W * 2 + 8, 26 + 24 * rows + 34), "");

        GUI.Label(new Rect(16, 12, W, 22), "<b>Catcher (抓捕者)</b>", Rich());
        GUI.Label(new Rect(16 + W + 8, 12, W, 22), "<b>Runner (逃跑者)</b>", Rich());

        for (int i = 0; i <= m_All.Length; i++)
        {
            int y = 34 + 24 * i;
            string label = i == 0 ? "None（默认/静止）" : m_All[i - 1].name;

            if (GUI.Button(new Rect(16, y, W, 22), (m_CatcherIdx == i ? "▶ " : "   ") + label))
                SetSelection(true, i);

            if (GUI.Button(new Rect(16 + W + 8, y, W, 22), (m_RunnerIdx == i ? "▶ " : "   ") + label))
                SetSelection(false, i);
        }

        // 演示用：只留第一个场地（Arena_V2_0），其余隐藏/恢复
        int by = 34 + 24 * rows + 6;
        if (GUI.Button(new Rect(16, by, W * 2, 24),
                       m_OthersHidden ? "显示全部场地（当前只显示 1 个）" : "只显示一个场地（演示用）"))
        {
            ToggleOtherArenas();
        }
    }

    static bool m_OthersHidden;
    static readonly List<GameObject> s_Hidden = new List<GameObject>();

    static void ToggleOtherArenas()
    {
        if (!m_OthersHidden)
        {
            // 不靠名字（场景里的实例叫 Arena_2 … Arena_V20，名字不统一），
            // 改成按组件识别：每个场地里正好有一个 Catcher。
            var all = new List<GameObject>();
            foreach (var agent in Object.FindObjectsOfType<CubeAgentV2>())
            {
                if (!agent.isCatcher) continue;
                var root = agent.transform.parent;
                if (root == null) continue;
                if (!all.Contains(root.gameObject)) all.Add(root.gameObject);
            }
            all.Sort((a, b) => a.transform.position.x.CompareTo(b.transform.position.x));
            if (all.Count == 0)
            {
                Debug.LogWarning("[ModelSwitcher] 没有找到任何场地");
                return;
            }

            s_Hidden.Clear();
            for (int i = 1; i < all.Count; i++)
            {
                if (!all[i].activeSelf) continue;
                all[i].SetActive(false);
                s_Hidden.Add(all[i]);
            }
            m_OthersHidden = true;
            Debug.Log($"[ModelSwitcher] 隐藏了 {s_Hidden.Count} 个场地，保留 {all[0].name}");
        }
        else
        {
            foreach (var go in s_Hidden) if (go != null) go.SetActive(true);
            Debug.Log($"[ModelSwitcher] 恢复 {s_Hidden.Count} 个场地");
            s_Hidden.Clear();
            m_OthersHidden = false;
        }
    }

    static GUIStyle s_Rich;
    static GUIStyle Rich()
    {
        if (s_Rich == null) s_Rich = new GUIStyle(GUI.skin.label) { richText = true };
        return s_Rich;
    }
}
