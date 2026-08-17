using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Vista.Editor
{
    /// <summary>
    /// 大气 LUT 预览窗口。参数调完即时重烘，配合 <see cref="VistaAtmosphereSelfTest"/> 的
    /// 数值比对，在 RenderGraph pass 存在之前就能验证 LUT 的正确性。
    /// </summary>
    public class VistaAtmosphereLutPreviewWindow : EditorWindow
    {
        [SerializeField] private VistaAtmosphereParameters m_Parameters = new VistaAtmosphereParameters();

        /// <summary>Sky-View 预览用的太阳仰角。3° 附近是最容易暴露参数化问题的角度。</summary>
        [SerializeField] private float m_SunElevation = 30f;
        /// <summary>Sky-View 预览用的相机海拔 (m)。</summary>
        [SerializeField] private float m_CameraAltitude = 2f;

        SerializedObject m_SerializedSelf;
        VistaAtmosphereLuts m_Luts;
        VistaAtmosphereSelfTest.Report m_Report;
        bool m_HasReport;
        Vector2 m_Scroll;

        /// <summary>Sky-View 的显示用副本（已做曝光 + tonemap + gamma）。</summary>
        Texture2D m_SkyPreview;
        float m_SkyPreviewMaxLuminance;

        [MenuItem("Window/Vista/Atmosphere LUT Preview")]
        static void Open() => GetWindow<VistaAtmosphereLutPreviewWindow>("Vista Atmosphere LUT");

        void OnEnable() => m_SerializedSelf = new SerializedObject(this);

        void OnDisable()
        {
            m_Luts?.Dispose();
            m_Luts = null;
            if (m_SkyPreview != null) DestroyImmediate(m_SkyPreview);
            m_SkyPreview = null;
            m_HasReport = false;
        }

        void OnGUI()
        {
            m_Scroll = EditorGUILayout.BeginScrollView(m_Scroll);

            m_SerializedSelf.Update();
            EditorGUILayout.PropertyField(
                m_SerializedSelf.FindProperty(nameof(m_Parameters)), new GUIContent("大气参数"), true);
            bool paramsChanged = m_SerializedSelf.ApplyModifiedProperties();

            EditorGUILayout.Space();
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("烘焙 / 刷新")) Rebuild();
                if (GUILayout.Button("恢复地球默认值"))
                {
                    m_Parameters = VistaAtmosphereParameters.CreateEarth();
                    m_SerializedSelf.Update();
                    Rebuild();
                }
            }
            if (paramsChanged && m_HasReport) Rebuild();

            if (m_HasReport)
            {
                EditorGUILayout.Space();
                EditorGUILayout.SelectableLabel(m_Report.text,
                    EditorStyles.textArea, GUILayout.Height(300f));

                if (m_Luts != null)
                {
                    DrawLut("Transmittance　X: 天顶 → 水平切线　Y: 地面 → 大气顶",
                        m_Luts.transmittanceLut,
                        VistaAtmosphereLuts.k_TransmittanceWidth,
                        VistaAtmosphereLuts.k_TransmittanceHeight, 512f);

                    DrawLut("Multi-Scattering　X: 太阳天顶角 cos −1 → 1　Y: 地面 → 大气顶",
                        m_Luts.multiScatteringLut,
                        VistaAtmosphereLuts.k_MultiScatteringSize,
                        VistaAtmosphereLuts.k_MultiScatteringSize, 256f);

                    DrawSkyViewSection();
                }
            }
            else
            {
                EditorGUILayout.HelpBox("点「烘焙 / 刷新」生成 LUT 并运行数值自检。", MessageType.Info);
            }

            EditorGUILayout.EndScrollView();
        }

        // Sky-View 存的是绝对亮度（cd/m²，量级 1e3~1e5），直接 GUI.DrawTexture 是一片纯白。
        // 所以这里读回来在 CPU 上走一遍"曝光 → Reinhard → gamma"，得到和最终画面同款的观感。
        // 192×108 的读回在 Editor 里可以忽略不计，换来的是拖太阳仰角时能直接看到晨昏色变。
        void DrawSkyViewSection()
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Sky-View（逐帧表）", EditorStyles.boldLabel);

            EditorGUI.BeginChangeCheck();
            m_SunElevation   = EditorGUILayout.Slider("太阳仰角 (°)", m_SunElevation, -10f, 90f);
            m_CameraAltitude = EditorGUILayout.Slider("相机海拔 (m)", m_CameraAltitude, 0f, 8000f);
            if (EditorGUI.EndChangeCheck() || m_SkyPreview == null)
                RefreshSkyPreview();

            if (m_SkyPreview == null) return;

            EditorGUILayout.LabelField(
                "X: 正对太阳 → 背对太阳　Y: 天顶 → 地平线(中线) → 天底　峰值 "
                + m_SkyPreviewMaxLuminance.ToString("F0") + " cd/m²");

            float w = Mathf.Min(EditorGUIUtility.currentViewWidth - 30f, 576f);
            float h = w * m_SkyPreview.height / m_SkyPreview.width;
            Rect r = GUILayoutUtility.GetRect(w, h, GUILayout.ExpandWidth(false));
            GUI.DrawTexture(r, m_SkyPreview, ScaleMode.StretchToFill, false);

            // 地平线在 uv.y = 0.5 处硬分段，画一条参考线：真机上这条线两侧应看不出接缝，
            // 但表里两侧本来就是不连续的（上方几百 km 路径 vs 下方几 km 就打到地面）。
            Handles.BeginGUI();
            Handles.color = new Color(1f, 1f, 1f, 0.35f);
            float midY = r.y + r.height * 0.5f;
            Handles.DrawLine(new Vector3(r.x, midY), new Vector3(r.xMax, midY));
            Handles.EndGUI();
        }

        void RefreshSkyPreview()
        {
            if (m_Luts == null || !m_Luts.isValid) return;

            float rad = m_SunElevation * Mathf.Deg2Rad;
            var sunDir = new Vector3(0f, Mathf.Sin(rad), Mathf.Cos(rad));
            var view = VistaAtmosphereViewData.Create(
                m_Parameters, new Vector3(0f, m_CameraAltitude, 0f), 0f, sunDir);

            var cmd = new CommandBuffer { name = "Vista SkyView (Preview)" };
            m_Luts.RenderSkyViewLut(cmd, view);
            Graphics.ExecuteCommandBuffer(cmd);
            cmd.Release();

            var hdr = VistaAtmosphereSelfTest.Readback(m_Luts.skyViewLut);
            int w = hdr.width, h = hdr.height;

            if (m_SkyPreview == null || m_SkyPreview.width != w || m_SkyPreview.height != h)
            {
                if (m_SkyPreview != null) DestroyImmediate(m_SkyPreview);
                // linear: false -> 这张贴图按 sRGB 解读，所以下面要自己做 gamma 编码
                m_SkyPreview = new Texture2D(w, h, TextureFormat.RGBA32, false, false)
                {
                    filterMode = FilterMode.Point,
                    hideFlags  = HideFlags.HideAndDontSave
                };
            }

            var src = hdr.GetPixels();
            var dst = new Color[src.Length];
            float maxLum = 0f;
            for (int i = 0; i < src.Length; ++i)
            {
                Color c = src[i];
                maxLum = Mathf.Max(maxLum, Mathf.Max(c.r, Mathf.Max(c.g, c.b)));

                // 与 VistaSky.shader 相同的曝光，保证预览和实机观感一致
                float e = view.exposure;
                float r = c.r * e, g = c.g * e, b = c.b * e;
                // Reinhard 只是占位：真机走 URP 的 tonemap。这里只需要"不过曝、能看出色相"
                r /= 1f + r; g /= 1f + g; b /= 1f + b;
                dst[i] = new Color(
                    Mathf.LinearToGammaSpace(r),
                    Mathf.LinearToGammaSpace(g),
                    Mathf.LinearToGammaSpace(b), 1f);
            }
            m_SkyPreview.SetPixels(dst);
            m_SkyPreview.Apply(false, false);
            m_SkyPreviewMaxLuminance = maxLum;

            DestroyImmediate(hdr);
        }

        static void DrawLut(string label, RTHandle handle, int texWidth, int texHeight, float maxDisplayWidth)
        {
            if (handle == null) return;

            EditorGUILayout.LabelField(label);
            float w = Mathf.Min(EditorGUIUtility.currentViewWidth - 30f, maxDisplayWidth);
            float h = w * texHeight / texWidth;
            Rect r = GUILayoutUtility.GetRect(w, h, GUILayout.ExpandWidth(false));
            // alphaBlend=false：LUT 的 a 通道是填充值，不能参与混合
            GUI.DrawTexture(r, handle, ScaleMode.StretchToFill, false);
        }

        void Rebuild()
        {
            m_Report = VistaAtmosphereSelfTest.Run(m_Parameters, ref m_Luts);
            m_HasReport = true;
            // 参数变了，SkyView 预览必须跟着重算（自检末尾已经把表刷成正午，
            // 但这里要按窗口上的仰角滑条重刷）
            RefreshSkyPreview();
            Repaint();
        }
    }
}
