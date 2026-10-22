// FBX2NPRRigEditors.cs — 변환한 프리팹의 손잡이(ToonFaceRig·PainterlyRig) 커스텀 인스펙터.
// 아티스트가 자주 만지는 값만 위에 묶고(스타일 모드, 잉크 외곽선, 얼굴 명암선, 림라이트, 눈매 잉크), 나머지는 '고급' 접기 안에 둔다.
// 값을 바꾸면 OnValidate → 리그의 Apply 가 재질/피처에 바로 넣으므로 씬 뷰에서 즉시 보인다. 텍스처에 굽는 값(피부 스무딩 등)은 '다시 변환' 버튼으로.
using System.IO;
using UnityEditor;
using UnityEngine;

namespace FBX2NPR.EditorTools
{
    public enum FBX2NPRStyle { SubcultureToon = 0, Painterly = 1 }

    static class RigEditorUtil
    {
        public static readonly GUIContent StyleLabel = new GUIContent("스타일 모드", "Subculture Toon = 얼굴 평탄화 + 이목구비 그리기 + 두 단 명암 + 먹선. Painterly = 붓질 알베도 + 부드러운 음영 + 얇은 잉크. 바꾸면 원본 모델을 그 모드로 다시 변환해 이 인스턴스를 교체한다 (6~50초).");

        public static string ModelPathOf(string guid) { return string.IsNullOrEmpty(guid) ? null : AssetDatabase.GUIDToAssetPath(guid); }
        public static FBX2NPRAssetSettings Settings(string modelPath)
        {
            var imp = AssetImporter.GetAtPath(modelPath);
            return FBX2NPRAssetSettings.FromJson(imp != null ? imp.userData : null) ?? new FBX2NPRAssetSettings();
        }
        public static void SaveSettings(string modelPath, FBX2NPRAssetSettings s)
        {
            var imp = AssetImporter.GetAtPath(modelPath); if (imp == null) return;
            imp.userData = s.ToJson(); EditorUtility.SetDirty(imp); AssetDatabase.WriteImportSettingsIfDirty(modelPath);
        }

        public static void Header(string text) { EditorGUILayout.Space(6); EditorGUILayout.LabelField(text, EditorStyles.boldLabel); }

        /// <summary>
        /// 필드 하나를 우리 이름표로 그린다. PropertyField 를 쓰면 필드에 붙은 [Header]/[Space] 장식까지 같이 그려져 구역 제목이 두 번 나오므로,
        /// 형에 맞는 컨트롤(슬라이더·토글·색)을 직접 그리고 [Range] 는 필드 속성에서 읽는다. BeginProperty 로 프리팹 오버라이드 표시·되돌리기는 유지.
        /// </summary>
        public static void Prop(SerializedObject so, string name, string label, string tip)
        {
            var p = so.FindProperty(name); if (p == null) { EditorGUILayout.HelpBox("필드 없음: " + name, MessageType.Warning); return; }
            var rect = EditorGUILayout.GetControlRect();
            var content = EditorGUI.BeginProperty(rect, new GUIContent(label, tip), p);
            switch (p.propertyType)
            {
                case SerializedPropertyType.Float:
                    {
                        RangeAttribute range = null;
                        var fi = so.targetObject.GetType().GetField(name, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                        if (fi != null) range = (RangeAttribute)System.Attribute.GetCustomAttribute(fi, typeof(RangeAttribute));
                        EditorGUI.BeginChangeCheck();
                        float v = range != null ? EditorGUI.Slider(rect, content, p.floatValue, range.min, range.max) : EditorGUI.FloatField(rect, content, p.floatValue);
                        if (EditorGUI.EndChangeCheck()) p.floatValue = v;
                        break;
                    }
                case SerializedPropertyType.Boolean: { EditorGUI.BeginChangeCheck(); bool v = EditorGUI.Toggle(rect, content, p.boolValue); if (EditorGUI.EndChangeCheck()) p.boolValue = v; break; }
                case SerializedPropertyType.Color: { EditorGUI.BeginChangeCheck(); var v = EditorGUI.ColorField(rect, content, p.colorValue); if (EditorGUI.EndChangeCheck()) p.colorValue = v; break; }
                default: EditorGUI.PropertyField(rect, p, content, false); break;
            }
            EditorGUI.EndProperty();
        }

        /// <summary>스타일 드롭다운. 현재 모드와 다른 것을 고르면 모델 설정의 style 을 바꾸고, 다시 변환해 이 씬 인스턴스를 새 프리팹으로 교체한다.</summary>
        public static void StyleSection(Component rig, FBX2NPRStyle current, string guid)
        {
            Header("스타일");
            string modelPath = ModelPathOf(guid);
            using (new EditorGUI.DisabledScope(modelPath == null))
            {
                var chosen = (FBX2NPRStyle)EditorGUILayout.EnumPopup(StyleLabel, current);
                if (chosen != current)
                {
                    if (EditorUtility.DisplayDialog("FBX2NPR 스타일 모드", Path.GetFileName(modelPath) + " 을(를) " + (chosen == FBX2NPRStyle.Painterly ? "회화풍" : "서브컬처 툰") + " 모드로 다시 변환해 이 인스턴스를 교체할까요? (파싱 포함 6~50초)", "변환해 교체", "취소"))
                        SwitchStyle(rig, modelPath, chosen);
                    GUIUtility.ExitGUI();
                }
            }
            if (modelPath == null) EditorGUILayout.HelpBox("원본 모델(sourceModelGuid)을 찾지 못해 스타일을 바꿀 수 없습니다.", MessageType.Info);
            else EditorGUILayout.LabelField("원본 모델", Path.GetFileName(modelPath), EditorStyles.miniLabel);
        }

        public static void SwitchStyle(Component rig, string modelPath, FBX2NPRStyle style)
        {
            var s = Settings(modelPath); s.style = (int)style; SaveSettings(modelPath, s);
            var root = PrefabUtility.GetOutermostPrefabInstanceRoot(rig.gameObject);
            if (root == null) root = rig.transform.root.gameObject;
            FBX2NPRConverter.Report rep;
            try { EditorUtility.DisplayProgressBar("FBX2NPR", "변환 중: " + Path.GetFileName(modelPath), 0.3f); rep = FBX2NPRConverter.Convert(modelPath); AssetDatabase.SaveAssets(); }
            finally { EditorUtility.ClearProgressBar(); }
            if (rep == null || rep.prefabPath == null) { EditorUtility.DisplayDialog("FBX2NPR", "변환 실패. 콘솔 기록을 보세요.\n" + rep, "확인"); return; }
            Debug.Log("[FBX2NPR] " + modelPath + "\n" + rep);
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(rep.prefabPath);
            var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab, root.scene);
            Undo.RegisterCreatedObjectUndo(go, "FBX2NPR 스타일 교체");
            go.transform.SetParent(root.transform.parent, false);
            go.transform.SetPositionAndRotation(root.transform.position, root.transform.rotation); go.transform.localScale = root.transform.localScale;
            go.transform.SetSiblingIndex(root.transform.GetSiblingIndex());
            Undo.DestroyObjectImmediate(root);
            var t = go.GetComponentInChildren<ToonFaceRig>(true); var p = go.GetComponentInChildren<PainterlyRig>(true);
            Selection.activeGameObject = t != null ? t.gameObject : p != null ? p.gameObject : go;
        }
    }

    [CustomEditor(typeof(ToonFaceRig))]
    public class ToonFaceRigEditor : Editor
    {
        static readonly string[] Curated = { "m_Script", "outline", "outlineDarkness", "outlineThickness", "linePosition", "chinTilt", "chinBend", "lineSoftness", "rim", "skinShadow", "bodyShadow", "bodyShadowColor" };
        bool advanced;

        public override void OnInspectorGUI()
        {
            var rig = (ToonFaceRig)target;
            serializedObject.Update();
            RigEditorUtil.StyleSection(rig, FBX2NPRStyle.SubcultureToon, rig.sourceModelGuid);

            RigEditorUtil.Header("잉크 외곽선 (화면공간 먹선)");
            RigEditorUtil.Prop(serializedObject, "outline", "외곽선 켜기", "화면공간 소벨 외곽선 피처를 켜고 끈다. 피처는 씬에 하나라 다른 리그에도 같이 적용된다.");
            using (new EditorGUI.DisabledScope(!rig.outline))
            {
                RigEditorUtil.Prop(serializedObject, "outlineDarkness", "먹선 진하기", "0 = 없음, 1 = 검정. 결과에 곱하므로 항상 어두워진다.");
                RigEditorUtil.Prop(serializedObject, "outlineThickness", "먹선 두께 (px)", "픽셀 단위라 거리와 상관없이 일정하다.");
            }
            if (!rig.driveOutlineSettings) EditorGUILayout.HelpBox("미리보기 씬 리그: 켜고 끄기만 하고 선 값은 렌더러 피처의 것을 씁니다.", MessageType.None);

            RigEditorUtil.Header("얼굴 명암선 (턱선)");
            RigEditorUtil.Prop(serializedObject, "linePosition", "명암선 위치", "클수록 명암선이 얼굴 안쪽(빛 쪽)으로 들어온다. -0.2 기본.");
            RigEditorUtil.Prop(serializedObject, "chinTilt", "턱선 기울기", "0 = 세로 한 줄, 클수록 명암선 아래쪽이 턱 끝까지 안쪽으로 휘어 들어온다.");
            RigEditorUtil.Prop(serializedObject, "chinBend", "휘는 구간", "작을수록 턱 가까이에서 급하게 꺾이고, 클수록 위에서부터 완만하게 휜다.");
            RigEditorUtil.Prop(serializedObject, "lineSoftness", "경계 부드러움", "작을수록 명암선이 딱 끊긴다.");

            RigEditorUtil.Header("림라이트");
            RigEditorUtil.Prop(serializedObject, "rim", "림라이트 밝기", "빛 쪽 윤곽선 안쪽을 살짝 밝게. 0 = 끔.");

            RigEditorUtil.Header("그림자 색");
            RigEditorUtil.Prop(serializedObject, "skinShadow", "피부 그림자 색", "얼굴 재질에만, 텍스처에 곱하는 리니어 값. 기본 회보라.");
            RigEditorUtil.Prop(serializedObject, "bodyShadow", "몸 그림자 폭", "몸·옷의 명암 경계 위치. 클수록 그림자가 넓어진다.");
            RigEditorUtil.Prop(serializedObject, "bodyShadowColor", "몸 그림자 색", "몸·옷 재질에 곱하는 리니어 값.");

            EditorGUILayout.Space(6);
            advanced = EditorGUILayout.Foldout(advanced, "고급 (전체 항목)", true);
            if (advanced) DrawPropertiesExcluding(serializedObject, Curated);

            if (serializedObject.ApplyModifiedProperties())
            {
                if (rig.isActiveAndEnabled && rig.gameObject.scene.IsValid()) { rig.ApplyOutline(); rig.Apply(); }
                SceneView.RepaintAll();
            }
        }
    }

    [CustomEditor(typeof(PainterlyRig))]
    public class PainterlyRigEditor : Editor
    {
        static readonly string[] Curated = { "m_Script", "inkLine", "inkThickness", "inkDarkness", "yieldToToon", "innerLineFade", "eyeInk" };
        bool advanced;

        public override void OnInspectorGUI()
        {
            var rig = (PainterlyRig)target;
            serializedObject.Update();
            RigEditorUtil.StyleSection(rig, FBX2NPRStyle.Painterly, rig.sourceModelGuid);

            RigEditorUtil.Header("잉크 외곽선 (화면공간 실루엣 선)");
            RigEditorUtil.Prop(serializedObject, "inkLine", "잉크 라인 세기", "0 = 없음, 1 = 기본. 실루엣(깊이 엣지)만 잡고 얼굴 안쪽 선은 그리지 않는다.");
            using (new EditorGUI.DisabledScope(rig.inkLine <= 0f))
            {
                RigEditorUtil.Prop(serializedObject, "inkThickness", "선 두께 (px)", "픽셀 단위.");
                RigEditorUtil.Prop(serializedObject, "inkDarkness", "선 진하기", "inkLine 이 1 일 때의 진하기.");
            }
            RigEditorUtil.Prop(serializedObject, "yieldToToon", "툰 리그에 양보", "같은 씬에 툰 모드 리그가 있으면 외곽선 값을 넣지 않는다 (피처는 씬에 하나).");
            if (rig.yieldToToon && PainterlyRig.ToonRigDrivesOutline()) EditorGUILayout.HelpBox("씬에 툰 모드 리그가 있어 잉크 외곽선 값은 툰 리그 것이 쓰입니다.", MessageType.Info);
            RigEditorUtil.Prop(serializedObject, "innerLineFade", "멀어지면 안쪽 선 지우기", "툰과 한 씬일 때(외곽선이 노멀 엣지를 쓸 때)만 쓰인다. x = 지우기 시작, y = 다 지움 — 픽셀 하나가 덮는 실제 크기(m). 변환 도구가 머리 높이로 채운다(머리 380→280픽셀). 0 이면 끔.");
            if (rig.WantsLineFade()) EditorGUILayout.HelpBox("외곽선이 노멀 엣지(안쪽 선)를 쓰고 있어, 화면에서 작아지면 이 캐릭터의 안쪽 선을 지웁니다.", MessageType.None);

            RigEditorUtil.Header("눈매 잉크");
            using (new EditorGUI.DisabledScope(!rig.HasEyeInk))
                RigEditorUtil.Prop(serializedObject, "eyeInk", "눈매 잉크 선명도", "원본의 아이라인·속눈썹을 붓질 피부 위에 Darken 으로 얹는 세기. 0 = 없음.");
            if (!rig.HasEyeInk) EditorGUILayout.HelpBox("이 프리팹은 눈매 잉크가 텍스처에 구워져 있어 슬라이더가 듣지 않습니다. 다시 변환하면 런타임 슬라이더가 됩니다.", MessageType.Info);
            else EditorGUILayout.LabelField("섞는 재질", rig.inkEntries.Count + "개", EditorStyles.miniLabel);

            RigEditorUtil.Header("림라이트");
            EditorGUILayout.HelpBox("회화풍 재질(FBX2NPR/PainterlyLit)은 조명이 URP Lit 과 같아 림라이트 항목이 아직 없습니다. 넣으려면 ForwardLit 패스를 바꿔야 합니다.", MessageType.None);

            string modelPath = RigEditorUtil.ModelPathOf(rig.sourceModelGuid);
            if (modelPath != null)
            {
                RigEditorUtil.Header("텍스처에 굽는 값 (바꾸면 다시 변환)");
                var s = RigEditorUtil.Settings(modelPath);
                EditorGUI.BeginChangeCheck();
                float smooth = EditorGUILayout.Slider(new GUIContent("얼굴 피부 스무딩", "1 = 붓털 줄무늬 끄고 패치 경계를 녹임(사진 피부), 0 = 피부도 옷처럼 붓질(손그림)."), s.painterlySkinSmooth, 0f, 1f);
                float ink0 = EditorGUILayout.Slider(new GUIContent("눈매 잉크 초기값", "변환 직후 리그 슬라이더의 초기값."), s.painterlyEyeInk, 0f, 1f);
                float decal = EditorGUILayout.Slider(new GUIContent("피부 장식 되살리기", "반창고·문신처럼 피부와 색조가 다른 작은 장식을 붓질 뒤 원본으로 되살리는 세기. 0 = 안 함."), s.painterlyDecalKeep, 0f, 1f);
                if (EditorGUI.EndChangeCheck()) { s.painterlySkinSmooth = smooth; s.painterlyEyeInk = ink0; s.painterlyDecalKeep = decal; RigEditorUtil.SaveSettings(modelPath, s); }
                if (GUILayout.Button("이 설정으로 다시 변환해 교체")) { RigEditorUtil.SwitchStyle(rig, modelPath, FBX2NPRStyle.Painterly); GUIUtility.ExitGUI(); }
            }

            EditorGUILayout.Space(6);
            advanced = EditorGUILayout.Foldout(advanced, "고급 (전체 항목)", true);
            if (advanced) DrawPropertiesExcluding(serializedObject, Curated);

            if (serializedObject.ApplyModifiedProperties())
            {
                if (rig.isActiveAndEnabled && rig.gameObject.scene.IsValid()) { rig.ApplyInk(); rig.ApplyEyeInk(); rig.ApplyLineFade(true); }
                SceneView.RepaintAll();
            }
        }
    }
}
