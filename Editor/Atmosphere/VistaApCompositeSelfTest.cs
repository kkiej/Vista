using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Vista.Editor
{
    /// <summary>
    /// Aerial Perspective 合成的**接线**自检。
    ///
    /// 刻意不验任何数值：合成公式只有一份实现（<c>AerialPerspectiveComposite.hlsl</c> 里
    /// 那个 <c>VistaApplyAerialPerspective</c>），在 C# 里重算一遍就等于造出第二份真相，
    /// 而两份走歧时报出的偏差既不是 0 也不是明显错误 —— 这个项目在反射自检那里
    /// 已经把这条教训写下来了。数值判据（距离连续性、变体 A/B 逐像素一致、性能）
    /// 属于 #15，走 GPU。
    ///
    /// 这里只覆盖三类「不报错但画面悄悄错」的失效：
    ///   1. shader 资源没解析到 / 有编译错误 —— 症状是 AP 整个不上画面，
    ///      而大气其余部分全都正常，看起来像「AP LUT 算错了」。
    ///   2. .shader 里的 Pass 顺序被调换 —— 乘和加互换，画面是远景雾偏暗，
    ///      三个序号仍然全部合法，没有任何报错。
    ///   3. 运行期没接通：全局 cbuffer 没被下发、或 _VistaApConsumer 与当前
    ///      compositeMode 不一致（变体 B 该开的没开、该关的没关）。
    ///      后者正是「关掉 AP 之后材质还拿着上一帧的 1」那条失效的直接观测点。
    /// </summary>
    public static class VistaApCompositeSelfTest
    {
        [MenuItem("Window/Vista/Validate AP Composite Wiring", priority = 126)]
        public static void Run()
        {
            var sb = new StringBuilder();
            bool ok = Validate(sb);

            Debug.Log(("[Vista] AP 合成接线自检" + (ok ? "通过" : "**失败**") + "\n" + sb)
                      .Replace("\r", "").Replace("\n", "  |  "));
        }

        static bool Validate(StringBuilder sb)
        {
            bool ok = true;

            // ── 判据 1：shader 资源与编译状态
            sb.AppendLine("── 判据 1：合成 shader");

            var resources = VistaRuntimeResources.Get();
            if (resources == null)
            {
                sb.AppendLine("　 **失败**：取不到 VistaRuntimeResources（当前管线不是 URP？）");
                return false;
            }

            var shader = resources.aerialPerspectiveCompositeShader;
            if (shader == null)
            {
                sb.AppendLine("　 **失败**：aerialPerspectiveCompositeShader 为 null。"
                            + "ResourcePath 解析失败 —— 检查 "
                            + "Shaders/Atmosphere/VistaAerialPerspectiveComposite.shader 是否存在。");
                return false;
            }

            sb.Append("　 shader = ").Append(shader.name).AppendLine();

            if (ShaderUtil.ShaderHasError(shader))
            {
                ok = false;
                sb.AppendLine("　 **失败**：shader 有编译错误：");
                foreach (var msg in ShaderUtil.GetShaderMessages(shader))
                    sb.Append("　　 ").Append(msg.severity).Append("　").Append(msg.message)
                      .Append("　@ ").Append(msg.file).Append(':').Append(msg.line).AppendLine();
            }
            else
            {
                sb.AppendLine("　 无编译错误 OK");
            }

            // ── 判据 2：Pass 序号 ↔ Pass 名
            sb.AppendLine("── 判据 2：Pass 声明顺序");

            var mat = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            try
            {
                int expected = VistaAerialPerspectiveCompositePass.k_PassNames.Length;
                sb.Append("　 passCount = ").Append(mat.passCount)
                  .Append("（预期 ").Append(expected).Append("）").AppendLine();

                if (mat.passCount != expected)
                {
                    ok = false;
                    sb.AppendLine("　 **失败**：pass 数量与运行时常量不符。");
                }

                int n = Mathf.Min(mat.passCount, expected);
                for (int i = 0; i < n; i++)
                {
                    string actual = mat.GetPassName(i);
                    string want   = VistaAerialPerspectiveCompositePass.k_PassNames[i];
                    bool match    = actual == want;
                    if (!match) ok = false;
                    sb.Append("　 [").Append(i).Append("] ").Append(actual)
                      .Append(match ? "　OK" : "　**≠ " + want + "**").AppendLine();
                }
            }
            finally
            {
                Object.DestroyImmediate(mat);
            }

            // ── 判据 3：运行期接线
            //
            // 只在有 feature 时判。没有 feature 不算失败 —— 这个菜单项也用于
            // 「装 feature 之前先确认 shader 没问题」的场合，那时报失败是噪声。
            sb.AppendLine("── 判据 3：运行期接线");

            var feature = VistaAtmosphereFeature.current;
            if (feature == null)
            {
                sb.AppendLine("　 (跳过) VistaAtmosphereFeature.current 为 null："
                            + "feature 没装进当前 Renderer，或还没有相机渲过一帧。");
                return ok;
            }

            var ap = feature.aerialPerspective;
            sb.Append("　 compositeMode = ").Append(ap.compositeMode).AppendLine();

            // 全局 cbuffer 是否被下发过。判 _VistaApSize 而不是 _VistaApParams：
            // 后者在极端配置下也可能全零，而尺寸恒 > 0，全零就一定意味着「AP pass 没跑」。
            var apSize = Shader.GetGlobalVector(VistaShaderIDs._VistaApSize);
            bool apPassRan = apSize.x > 0f && apSize.y > 0f && apSize.z > 0f;
            sb.Append("　 _VistaApSize = ").Append(apSize.ToString("F0"))
              .Append(apPassRan ? "　AP pass 跑过 OK" : "　**AP pass 没跑过**").AppendLine();

            // 变体 B 的开关。这一行是「关掉 AP 之后材质还拿着上一帧的 1」的观测点：
            // 期望值只由 compositeMode 决定，与 AP 表在不在无关 ——
            // 因为下发它的是 Sky-View pass，而那个 pass 每帧都跑。
            var consumer = Shader.GetGlobalVector(VistaShaderIDs._VistaApConsumer);
            float wantConsumer = ap.compositeMode
                                 == VistaAerialPerspectiveSettings.CompositeMode.InShader ? 1f : 0f;
            // AP 表本身不可用时（核缺失 / 分级降档）即使选了 InShader 也必须是 0。
            if (!apPassRan) wantConsumer = 0f;

            bool consumerOk = Mathf.Abs(consumer.x - wantConsumer) < 1e-6f;
            if (!consumerOk) ok = false;
            sb.Append("　 _VistaApConsumer.x = ").Append(consumer.x.ToString("F1"))
              .Append("（预期 ").Append(wantConsumer.ToString("F1")).Append("）")
              .Append(consumerOk ? "　OK" : "　**不符**").AppendLine();

            if (!consumerOk)
                sb.AppendLine("　 排查顺序：Sky-View pass 是否被剪（Frame Debugger 里找 "
                            + "\"Vista Sky-View LUT\"）→ PackedConsumer 的 lutsValid 实参 "
                            + "→ 是否有别处也在写 _VistaApConsumer。");

            sb.AppendLine("　 备注：Fullscreen 那趟在图里的名字是 "
                        + "\"Vista Aerial Perspective Composite\"，"
                        + "本自检不判它在不在 —— 那要靠 Frame Debugger 或 #15 的性能项。");

            return ok;
        }
    }
}
