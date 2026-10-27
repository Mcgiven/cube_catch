// ReplaceArenaV2.cs —— 菜单栏一键操作：把场景里所有 Arena_1 实例换成 Arena_V2。
// 放在 Assets/Editor/ 下，Unity 会自动编译成菜单项。
//
// 用法：打开 multiple arena 场景 → 菜单 Tools → Replace Arena_1 with Arena_V2
using System.Collections.Generic;
using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;

public static class ReplaceArenaV2
{
    const string V1Path = "Assets/Prefabs/Arena_1.prefab";
    const string V2Path = "Assets/Prefabs/Arena_V2.prefab";

    [MenuItem("Tools/Replace Arena_1 with Arena_V2")]
    public static void Replace()
    {
        var v2 = AssetDatabase.LoadAssetAtPath<GameObject>(V2Path);
        if (v2 == null)
        {
            EditorUtility.DisplayDialog("替换失败", "找不到 " + V2Path + "\n请先把 Arena_1 复制一份并改名 Arena_V2。", "好");
            return;
        }

        // 先收集，再改，避免边遍历边销毁
        var targets = new List<Transform>();
        foreach (var t in Object.FindObjectsOfType<Transform>(true))
        {
            var src = PrefabUtility.GetCorrespondingObjectFromSource(t.gameObject);
            if (src == null) continue;
            if (AssetDatabase.GetAssetPath(src) != V1Path) continue;
            targets.Add(t);
        }

        if (targets.Count == 0)
        {
            EditorUtility.DisplayDialog("替换失败", "当前场景里没有找到 Arena_1 的实例。\n先打开 multiple arena 场景。", "好");
            return;
        }

        int n = 0;
        foreach (var t in targets)
        {
            var parent = t.parent;
            var go = (GameObject)PrefabUtility.InstantiatePrefab(v2, parent);
            go.transform.localPosition = t.localPosition;
            go.transform.localRotation = t.localRotation;
            go.transform.localScale = t.localScale;
            go.name = t.name.Replace("Arena_1", "Arena_V2");
            Object.DestroyImmediate(t.gameObject);
            n++;
        }

        EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
        Debug.Log($"[ReplaceArenaV2] 已替换 {n} 个实例。检查 X 坐标是否仍为 0 / 30 / 60 … 330。");
    }
}
