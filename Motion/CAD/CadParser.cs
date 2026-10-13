using Core.Motion;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using ZMotionSDK;
using ZmotionCadDll;

namespace Motion.CAD
{
    /// <summary>
    /// CAD 导图 → 轨迹段的转换选项。
    /// </summary>
    public sealed class CadParseOptions
    {
        /// <summary>默认选项（整份图纸都转）。</summary>
        public static CadParseOptions Default { get; } = new CadParseOptions();

        /// <summary>
        /// 只转换 CAD 里被选中的图元（对应样例的"仅处理选中图形"）。
        /// </summary>
        /// <remarks>
        /// 用途是"先把一部分图形调好，再只下这一部分"。注意选中集是图元级的：
        /// 一条曲线的中间几段没被选中时，曲线会被断开，断开处自动生成一条空移段
        /// （见 <see cref="CadParser.Convert"/>），不会拉出一条图纸上没有的直线。
        /// </remarks>
        public bool OnlySelected { get; init; }
    }

    /// <summary>
    /// 一次 CAD 导图的结果：轨迹段 + 必须让现场看到的告警。
    /// </summary>
    /// <param name="Segments">按加工顺序排列的轨迹段（点胶段 + 曲线之间的空移段）</param>
    /// <param name="Warnings">几何被丢弃 / 退化 / 未处理图元的说明；空列表表示无话可说</param>
    /// <remarks>
    /// 之所以连告警一起返回，而不是"悄悄按直线处理完"：导图是胶路的源头，
    /// 一段本该是圆弧的胶路退化成弦线，或者整类图元被跳过，属于"能跑但会出次品"，
    /// 现场必须看到清单再决定改图纸还是改参数（与 <c>TrajectoryEditor.ValidateAllSegments</c> 同一套取舍）。
    /// </remarks>
    public sealed record CadParseResult(
        IReadOnlyList<TrajectorySegment> Segments, IReadOnlyList<string> Warnings);

    /// <summary>
    /// 正运动 CAD 图形库（<c>ZmotionCadEx.dll</c>）的托管封装：把 CAD 图纸读成运动域的
    /// <see cref="TrajectorySegment"/>。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>与 <see cref="DxfParser"/> 的分工</b>：两者是上下游两件事，不是重复实现。
    /// <c>DxfParser</c> 用 netDxf 读 <b>DXF 格式本身</b>——离线可用（不需要卡）、圆弧保留原生几何，
    /// 但不认识 PLT / AI / DST，也没有图纸编辑能力；本类是运动卡厂商的 CAD 图形库，
    /// 能读 DXF / PLT / AI / DST，并且库内自带选择、排序、平滑、去重、合并、优化空移这一整套
    /// "导图"操作（样例 <c>WindowsFormsApp1</c> 的 CAD 界面就是它）。
    /// 换型图纸是 DXF 时两条路都能走；PLT / AI 只能走这里。
    /// </para>
    /// <para>
    /// <b>图元数组是进程全局的——这是本类最要紧的一条</b>：
    /// <c>ZMotionCadArray_ImportVectGraph / GetVectNum / GetVectArray</c> 的参数里<b>没有句柄</b>，
    /// 图形数据存在动态库自己的全局区里；<see cref="ConnectEther"/> 的句柄只负责解锁"高级功能"
    /// （选择 / 排序 / 平滑）。由此有两条硬约束：
    /// <list type="number">
    /// <item>两个 <see cref="CadParser"/> 实例看到的是<b>同一份图形</b>，谁导入谁覆盖——
    /// 所以别把"某个实例的图纸"当成私有状态缓存起来；</item>
    /// <item>所有原生调用由本类的<b>静态锁</b>串行化。这不只是防重入，更是防
    /// "取数量"与"取数组"之间被插进一次导入：那会让库往只申请了 N 个元素的托管数组里写更多数据，
    /// 直接踩坏托管堆（比抛异常难查得多）。</item>
    /// </list>
    /// </para>
    /// <para>
    /// <b>连接先于导入</b>：样例（<c>ZCadFn.ZcadReadFile</c>）要求 <c>OpenEth</c> 成功才允许导入，
    /// 本类照做。<c>待核</c>：导入函数签名里没有句柄，库内部很可能仍按"是否已连接"决定行为——
    /// 真机确认"不连接也能导入"后，去掉 <see cref="LoadCadFile"/> 里的连接检查就能离线导图。
    /// </para>
    /// <para>
    /// <b>只转几何，不带工艺</b>：图纸里没有速度、工作高度、抬刀高度，
    /// 一律用 <see cref="TrajectorySegment"/> 的默认值，由配方编辑器 /
    /// <c>TrajectoryEditor.BatchSetProperty</c> 统一设——与 <see cref="DxfParser"/> 的行为一致。
    /// </para>
    /// <para>
    /// <b>平台</b>：<c>ZmotionCadEx.dll</c> 是 x64 原生库，宿主必须是 x64 进程（App 已配 x64）；
    /// AnyCPU / x86 下调用会抛 <c>BadImageFormatException</c>。
    /// </para>
    /// <para>
    /// <b>本类不封装</b>选择 / 排序 / 平滑 / 去重 / 合并 / 分割 / 改起点 / 优化空移这些编辑操作：
    /// 它们是"导图界面"的职责，等界面搬过来时按样例 <c>ZCadFn</c> 的调用方式在本类加一层即可，
    /// 现在加就是没人调的空转代码。
    /// </para>
    /// </remarks>
    public sealed class CadParser : IDisposable
    {
        /// <summary>
        /// <c>ImportVectGraph</c> 的 Option：曲线<b>强制转成小线段</b>（样例用法）。
        /// </summary>
        /// <remarks>
        /// 这个模式下圆弧也在库里被拆成一串 <c>VECTLine</c>，精度由 <c>refDistance</c> 决定
        /// （样例取 <c>0.001</c>）。好处是任何卡都能跑；代价是丢掉原生圆弧插补，
        /// 段数也随图纸复杂度暴涨。手册对非 0 的 Option 没有说明（"预留"），故只定义这一个值。
        /// </remarks>
        public const int OptionCurvesToSegments = 0;

        /// <summary>图纸平面的 Z：图纸是二维的，高度是工艺参数（<c>WorkZ</c> / <c>LiftZ</c>），不是几何</summary>
        private const double DrawingPlaneZ = 0;

        /// <summary>圆弧字段自洽性容差（相对半径）</summary>
        private const double ArcRadiusTolerance = 1e-6;

        /// <summary>角度 / 长度退化判定阈值</summary>
        private const double ArcEpsilon = 1e-9;

        /// <summary>端点连续性判定阈值（mm），与 <c>DispenseProgram</c> 的下发校验同一口径</summary>
        private const double ContinuityEpsilon = 1e-6;

        /// <summary>图形库全局锁：见类说明——既是重入保护，也防"取数量 / 取数组"被打断</summary>
        private static readonly object LibraryGate = new object();

        private readonly object gate = new object();

        private IntPtr handle = IntPtr.Zero;
        private string? endpoint;

        /// <summary>CAD 图形库句柄；未连接时为 <see cref="IntPtr.Zero"/>。</summary>
        public IntPtr Handle
        {
            get
            {
                lock (gate)
                {
                    return handle;
                }
            }
        }

        /// <summary>图形库是否已连接。</summary>
        public bool IsConnected => Handle != IntPtr.Zero;

        /// <summary>已连接的卡地址；未连接时为 <c>null</c>。</summary>
        public string? Endpoint
        {
            get
            {
                lock (gate)
                {
                    return endpoint;
                }
            }
        }

        // ───────────────────────── 连接 ─────────────────────────

        /// <summary>
        /// 连接控制器以解锁图形库的高级功能（选择 / 排序 / 平滑）。
        /// </summary>
        /// <param name="ip">卡地址（<c>192.168.x.x</c>；正运动仿真器为 <c>127.0.0.1</c>）</param>
        /// <remarks>
        /// <b>幂等</b>：已连接同一地址时直接返回，<b>绝不再连一次</b>。
        /// 样例的 <c>ZCadIni</c> 是 <c>iret += OpenEth(...)</c>，无条件再连就会多出一个句柄占着同一张卡，
        /// 后面谁也说不清该关哪个；已连接 A 却要求连 B 时直接抛异常，防"悄悄换通道"。
        /// 这与 <c>ZmotionCardHost</c> 对运动卡连接的处理是同一条约定。
        /// </remarks>
        public void ConnectEther(string ip)
        {
            if (string.IsNullOrWhiteSpace(ip))
            {
                throw new ArgumentException("卡地址不能为空", nameof(ip));
            }

            lock (gate)
            {
                if (handle != IntPtr.Zero)
                {
                    if (!string.Equals(endpoint, ip, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException(
                            $"CAD 图形库已连接到 {endpoint}，拒绝改连 {ip}（同一张卡只能有一个连接）");
                    }

                    return;
                }

                lock (LibraryGate)
                {
                    Check(ZmotionCad.ZMotionCadArray_OpenEth(ip, out IntPtr opened), $"连接 {ip}");

                    handle = opened;
                    endpoint = ip;
                }
            }
        }

        /// <summary>
        /// 断开图形库连接（幂等）。
        /// </summary>
        /// <remarks>
        /// 关闭走 <c>ZAux_Close</c>：CAD 库已并入正运动的 ZAux 家族，句柄是同一套连接池里的，
        /// 样例 <c>Form1</c> 关 CAD 界面也是用这个函数关的。
        /// 返回值不作判断——句柄无论如何都要丢掉，非 0 只说明它本就失效了；
        /// 这里没有"成功"可言，也就谈不上假成功。
        /// </remarks>
        public void Disconnect()
        {
            lock (gate)
            {
                if (handle == IntPtr.Zero)
                {
                    return;
                }

                lock (LibraryGate)
                {
                    zmcaux.ZAux_Close(handle);
                    handle = IntPtr.Zero;
                    endpoint = null;
                }
            }
        }

        // ───────────────────────── 导图 ─────────────────────────

        /// <summary>
        /// 导入图形文件（DXF / PLT / AI / DST），覆盖图形库里的当前图纸。
        /// </summary>
        /// <param name="filePath">图纸文件路径</param>
        /// <param name="refDistance">转换参考精度（曲线离散的粒度）；样例的曲线模式用 <c>0.001</c></param>
        /// <param name="duUnit">PLT 的比例，DXF 用默认 <c>1024</c></param>
        /// <param name="option">导入选项，见 <see cref="OptionCurvesToSegments"/></param>
        /// <exception cref="InvalidOperationException">未连接，或图形库返回非 0 错误码</exception>
        /// <exception cref="FileNotFoundException">文件不存在</exception>
        /// <remarks>
        /// 导入是<b>整份覆盖</b>：图形库只有一份全局图纸，不会叠加到上一份上。
        /// 原实现的 <c>File.Exists</c> 判断写反了（文件存在才抛 <c>FileNotFoundException</c>），
        /// 结果是"存在的文件永远导不进来"。
        /// </remarks>
        public void LoadCadFile(
            string filePath,
            double refDistance = 0.05,
            double duUnit = 1024,
            int option = OptionCurvesToSegments)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                throw new ArgumentException("图纸路径不能为空", nameof(filePath));
            }

            if (refDistance <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(refDistance), refDistance, "参考精度必须为正");
            }

            if (!File.Exists(filePath))
            {
                throw new FileNotFoundException($"CAD 文件不存在: {filePath}", filePath);
            }

            EnsureConnected();

            string fullPath = Path.GetFullPath(filePath);

            lock (LibraryGate)
            {
                Check(
                    ZmotionCad.ZMotionCadArray_ImportVectGraph(fullPath, duUnit, option, refDistance),
                    $"导入图纸 {fullPath}");
            }
        }

        /// <summary>
        /// 读取图形库里当前的图元数组（导入后调用；库被编辑过时每次读到的都是最新状态）。
        /// </summary>
        /// <remarks>
        /// <c>GetVectArray</c> 的签名是"指向首元素的指针 + 数量"，所以必须传
        /// <c>ref entries[0]</c>：传数组本身在 CLR 里编不过（<c>ref T[]</c> 不是 <c>ref T</c>），
        /// 而只传一个元素会让库只填一条、其余全是零值——那种"读出来全是 (0,0)"最难查。
        /// 两次调用在同一把锁内完成，原因见类说明。
        /// </remarks>
        public IReadOnlyList<ZmotionCad.Struct_ZCad_Array> GetAllEntry()
        {
            EnsureConnected();

            lock (LibraryGate)
            {
                int length = 0;
                Check(ZmotionCad.ZMotionCadArray_GetVectNum(ref length), "读取图元数量");

                if (length <= 0)
                {
                    return Array.Empty<ZmotionCad.Struct_ZCad_Array>();
                }

                var entries = new ZmotionCad.Struct_ZCad_Array[length];
                Check(
                    ZmotionCad.ZMotionCadArray_GetVectArray(ref entries[0], length),
                    $"读取 {length} 个图元");

                return entries;
            }
        }

        /// <summary>
        /// 图纸范围（坐标方向向上为正）。
        /// </summary>
        /// <param name="refDistance">参考精度，与导入时保持一致</param>
        /// <remarks>
        /// 空图纸时全为 0；样例把空图纸兜底成 100×100，那是"绘图视图别除零"的事，
        /// 属于界面职责，这里如实返回 0，不替调用方编造尺寸。
        /// 原实现返回的是未定义的 <c>f</c>（高度写成 <c>f</c>），编译都过不去。
        /// </remarks>
        public (float Left, float Bottom, float Width, float Height) GetBoundary(double refDistance = 0.05)
        {
            EnsureConnected();

            float left = 0;
            float bottom = 0;
            float width = 0;
            float height = 0;

            lock (LibraryGate)
            {
                Check(
                    ZmotionCad.ZMotionCadArray_GetRange(ref left, ref bottom, ref width, ref height, refDistance),
                    "读取图纸范围");
            }

            return (left, bottom, width, height);
        }

        /// <summary>
        /// 导图：读图形库当前图纸，转成轨迹段。
        /// </summary>
        /// <param name="options">转换选项，缺省整份图纸都转</param>
        /// <returns>轨迹段与告警；调用方拿到后交给 <c>DispenseProgram.LoadSegments</c> 即可</returns>
        public CadParseResult Parse(CadParseOptions? options = null) => Convert(GetAllEntry(), options);

        /// <summary>
        /// 图元数组 → 轨迹段（纯函数，不碰原生库，可离线测试）。
        /// </summary>
        /// <param name="entries">图形库的图元数组</param>
        /// <param name="options">转换选项，缺省整份图纸都转</param>
        /// <remarks>
        /// <para><b>转换规则：</b>
        /// <list type="bullet">
        /// <item><c>m_nInVectFrist = 1</c> 是"某条曲线的第一个图元"，即曲线边界：
        /// 一条曲线 = 一串首尾相接的点胶段；</item>
        /// <item>每条曲线之前补一条 <see cref="MotionMode.Move"/> 空移段（样例 <c>Code_StartString</c> 的
        /// "空移到起点 + 落刀"）：抬刀状态走到曲线起点，再换成点胶段落刀，
        /// 这样"空移"与"点胶"在配方里是两种段，卡侧一眼能分开；</item>
        /// <item>几何上接不上的地方（只选了一部分图形、图纸本身断开）也会落一条空移段：
        /// <b>绝不把两点之间连成直线</b>——那条线图纸上没有，卡上就是一次撞针或一路滴胶。</item>
        /// </list>
        /// </para>
        /// <para>
        /// 本项目的方案不把轨迹平滑成小线段，而是把直线 / 圆弧原样交给卡做原生插补，
        /// 所以拐角不需要拆——拆了反而多出无谓的抬刀落刀。
        /// </para>
        /// <para>
        /// <b>图元类型</b>：<c>VECTLine</c> / <c>VECTArc</c> 转成轨迹段；
        /// <c>VECT</c> 是曲线容器标记，无几何；<c>VECTPoint</c> 是单独点或空移点，
        /// 样例同样只画不加工（本模型没有"原地出胶一次"的段表达，故记告警让人知道漏了什么）；
        /// 其余类型（样条 / 椭圆 / 贝塞尔 / 文本）整条跳过并告警一次——一个不支持的图元
        /// 不该废掉整份图纸，但也不能当它不存在。
        /// </para>
        /// </remarks>
        ///
        ///根据每个曲线的第一个图元标记 <c>m_nInVectFrist = 1</c>，把图元数组拆成一条条曲线： 
        /// 同一个曲线的图元首尾相接，曲线之间不接。
        /// 每条曲线前面补一条空移段，曲线本身是点胶段。
        /// 同一个曲线先存放到 <c>curve</c>，遇到下一条曲线或几何不连续时，添加空移段，收掉当前曲线，存入 <c>segments</c>。
        /// 
        ///
        public static CadParseResult Convert(
            IReadOnlyList<ZmotionCad.Struct_ZCad_Array> entries,
            CadParseOptions? options = null)
        {
            if (entries is null)
            {
                throw new ArgumentNullException(nameof(entries));
            }

            options ??= CadParseOptions.Default;

            var segments = new List<TrajectorySegment>();
            var warnings = new List<string>();
            var skippedTypes = new HashSet<int>();
            var curve = new List<TrajectorySegment>();

            Point3D arrived = default;
            bool hasArrived = false;
            int dotCount = 0;

            // 收掉当前曲线：先落一条空移段，再落曲线自己的点胶段
            void Flush()
            {
                if (curve.Count == 0)
                {
                    return;
                }

                // 第一条曲线的空移起点取它自己的起点（长度 0）：机器此刻在哪儿本函数不知道，
                // "从当前位置绝对空移过去"是卡侧 / 组合根的事。样例无条件先 MOVEABSSP 到起点再落刀，
                // 结构上与本段序一致——每条点胶路径前面都有一条空移。
                Point3D from = hasArrived ? arrived : curve[0].StartPt;

                segments.Add(new TrajectorySegment
                {
                    SegType = SegmentType.Line,
                    MotionMode = MotionMode.Move,
                    StartPt = from,
                    EndPt = curve[0].StartPt
                });

                segments.AddRange(curve);

                arrived = curve[curve.Count - 1].EndPt;
                hasArrived = true;
                curve.Clear();
            }

            void Append(TrajectorySegment segment)
            {
                // 标志说"还在同一条曲线"，几何却接不上（典型场景：只选中了曲线的一段）：
                // 先收掉当前曲线，让断开处落一条空移，而不是硬连成一条直线
                if (curve.Count > 0 && !IsSamePlace(curve[curve.Count - 1].EndPt, segment.StartPt))
                {
                    Flush();
                }

                curve.Add(segment);
            }

            for (int i = 0; i < entries.Count; i++)
            {
                ZmotionCad.Struct_ZCad_Array item = entries[i];

                if (options.OnlySelected && item.m_nChoose == 0)
                {
                    continue;
                }

                // 曲线边界：样例也是靠这个标志给每条曲线补"空移 + 落刀"
                if (item.m_nInVectFrist == 1)
                {
                    if (curve.Count > 0)//当两曲线首尾重合，不产生空移段
                    {
                        Flush();
                    }
                }

                switch (item.m_nItemtype)
                {
                    case ZmotionCad.ZCAD_ITEMTYPE_VECTLine:
                        Append(CreateLine(item, MotionMode.Dispense));//添加点胶线段
                        break;

                    case ZmotionCad.ZCAD_ITEMTYPE_VECTArc:
                        if (TryCreateArc(item, out TrajectorySegment? arc, out string reason))
                        {
                            Append(arc!);//添加点胶弧段
                        }
                        else
                        {
                            // 宁可退化 + 告警，也不静默瞎画：弦线会切掉内角（胶路少一截），
                            // 现场必须知道这一条弧没走成弧
                            warnings.Add(
                                $"第 {i} 个图元是圆弧，但几何重建不成立（{reason}），已按起终点连成的直线处理：" +
                                "该处胶路会由圆弧退化成弦线，请核对图纸，或改用 Option=0（曲线转小线段）重新导入");
                            Append(CreateLine(item, MotionMode.Dispense));
                        }

                        break;

                    case ZmotionCad.ZCAD_ITEMTYPE_VECT:
                        // 曲线容器标记，本身不带几何
                        break;

                    case ZmotionCad.ZCAD_ITEMTYPE_VECTPoint:
                        // 空移点（m_nEmptyMove != 0）与单独点：都不产生加工段
                        if (item.m_nEmptyMove == 0)
                        {
                            dotCount++;
                        }

                        break;

                    default:
                        if (skippedTypes.Add(item.m_nItemtype))
                        {
                            warnings.Add(
                                $"第 {i} 个图元的类型 {item.m_nItemtype} 本实现不处理（样条 / 椭圆 / 贝塞尔 / 文本一类），" +
                                "这类图元已整条跳过；请在 CAD 侧把它们炸开成直线 / 圆弧，或用 Option=0 让库先转成小线段");
                        }

                        break;
                }
            }

            Flush();

            if (dotCount > 0)
            {
                warnings.Add(
                    $"图纸里有 {dotCount} 个单独点图元未生成轨迹段：本模型没有\"原地出胶一次\"的段表达，" +
                    "样例同样只画不加工；若工艺确实要点出胶，请把点画成极短的直线或改用空移点标注");
            }

            return new CadParseResult(segments, warnings);
        }

        /// <summary>
        /// 把图元数组导出成 CSV（图纸核对 / 追溯用）。
        /// </summary>
        /// <param name="savePath">目标文件路径</param>
        /// <param name="data">要导出的图元，通常来自 <see cref="GetAllEntry"/></param>
        /// <remarks>
        /// 带 UTF-8 BOM 写出：Excel 打开中文表头不会乱码。
        /// 数字一律 <see cref="CultureInfo.InvariantCulture"/>——CSV 用逗号分列，
        /// 小数点是逗号的区域会直接把一列拆成两列。
        /// 原实现的 <c>$"{item.y1:}:F4"</c> 是坏的插值格式（既丢精度又把字面量写进文件）。
        /// </remarks>
        public void ExportPointCsv(string savePath, IReadOnlyList<ZmotionCad.Struct_ZCad_Array> data)
        {
            if (string.IsNullOrWhiteSpace(savePath))
            {
                throw new ArgumentException("导出路径不能为空", nameof(savePath));
            }

            if (data is null)
            {
                throw new ArgumentNullException(nameof(data));
            }

            using var writer = new StreamWriter(
                savePath, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

            writer.WriteLine("Index,ItemType,GroupNum,IsCurveStart,IsSelected,StartX,StartY,EndX,EndY");

            for (int i = 0; i < data.Count; i++)
            {
                ZmotionCad.Struct_ZCad_Array item = data[i];

                writer.WriteLine(string.Join(
                    ',',
                    i.ToString(CultureInfo.InvariantCulture),
                    item.m_nItemtype.ToString(CultureInfo.InvariantCulture),
                    item.m_nGroupNum.ToString(CultureInfo.InvariantCulture),
                    item.m_nInVectFrist.ToString(CultureInfo.InvariantCulture),
                    item.m_nChoose.ToString(CultureInfo.InvariantCulture),
                    item.x1.ToString("F4", CultureInfo.InvariantCulture),
                    item.y1.ToString("F4", CultureInfo.InvariantCulture),
                    item.x2.ToString("F4", CultureInfo.InvariantCulture),
                    item.y2.ToString("F4", CultureInfo.InvariantCulture)));
            }
        }

        /// <summary>释放连接（等同 <see cref="Disconnect"/>）。</summary>
        public void Dispose() => Disconnect();

        // ───────────────────────── 内部：几何 ─────────────────────────

        /// <summary>图元的起终点 → 直线段（Z 取图纸平面，高度由 <c>WorkZ</c> / <c>LiftZ</c> 表达）</summary>
        private static TrajectorySegment CreateLine(in ZmotionCad.Struct_ZCad_Array item, MotionMode mode) =>
            new TrajectorySegment
            {
                SegType = SegmentType.Line,
                MotionMode = mode,
                StartPt = new Point3D(item.x1, item.y1, DrawingPlaneZ),
                EndPt = new Point3D(item.x2, item.y2, DrawingPlaneZ)
            };

        /// <summary>
        /// 由圆弧图元重建 <see cref="SegmentType.Arc"/> 段。
        /// </summary>
        /// <param name="item">圆弧图元</param>
        /// <param name="arc">重建出的圆弧段（逆时针表示）</param>
        /// <param name="reason">失败原因，供告警用</param>
        /// <returns>几何自洽时返回 true，否则 false（由调用方退化处理并告警）</returns>
        /// <remarks>
        /// <para>
        /// <b>字段语义</b>（见 <c>ZmotionCadEx</c> 的 struct 注释）：<c>x1/y1</c> 段起点、
        /// <c>x2/y2</c> 段终点、<c>z1</c> 起始角、<c>z2</c> 旋转角（弧度）；
        /// 旋转角按 <c>ZMotionOptimize_TransArcSeges</c> 的约定<b>顺时针为正</b>，
        /// 与 <see cref="TrajectorySegment"/> "始终逆时针" 的约定相反，所以顺时针弧要把起终点对调。
        /// <c>待核</c>：真机导入一份带圆弧的图纸核对这组语义——样例导入用 Option=0，
        /// 圆弧在库里就被拆成小线段了，<b>这条分支在样例里从没跑到过</b>，属于按手册注释实现。
        /// </para>
        /// <para>
        /// <b>因此必须有自洽性校核</b>：半径由弦长反算（<c>弦长 = 2r·sin(扫掠角/2)</c>），
        /// 再用"终点必须落在同一个圆上"验证这一组假设。验不过就<b>不返回几何</b>，
        /// 让调用方退化成弦线并告警——绝不"反正是个弧，凑合画"，
        /// 那等于静默改掉胶路而没人知道。
        /// </para>
        /// </remarks>
        private static bool TryCreateArc(
            in ZmotionCad.Struct_ZCad_Array item,
            out TrajectorySegment? arc,
            out string reason)
        {
            arc = null;

            double startAngle = item.z1;
            double rotation = item.z2;
            double sweep = Math.Abs(rotation);

            if (sweep < ArcEpsilon)
            {
                reason = "旋转角为 0";
                return false;
            }

            if (sweep >= 2 * Math.PI - ArcEpsilon)
            {
                reason = "整圆：起终点重合，半径无法由两点反算";
                return false;
            }

            double dx = item.x2 - item.x1;
            double dy = item.y2 - item.y1;
            double chord = Math.Sqrt(dx * dx + dy * dy);

            if (chord < ArcEpsilon)
            {
                reason = "起终点重合（整圆或退化图元）";
                return false;
            }

            double radius = chord / (2 * Math.Sin(sweep / 2));

            if (double.IsNaN(radius) || double.IsInfinity(radius) || radius <= 0)
            {
                reason = "半径反算失败";
                return false;
            }

            // 起点的半径方向就是起始角，圆心在起点反方向一个半径处
            double centerX = item.x1 - radius * Math.Cos(startAngle);
            double centerY = item.y1 - radius * Math.Sin(startAngle);

            double endRadius = Math.Sqrt(
                (item.x2 - centerX) * (item.x2 - centerX) + (item.y2 - centerY) * (item.y2 - centerY));

            double tolerance = Math.Max(1e-6, radius * ArcRadiusTolerance);
            if (Math.Abs(endRadius - radius) > tolerance)
            {
                reason = string.Format(
                    CultureInfo.InvariantCulture,
                    "终点不在重建出的圆上（半径 {0:F6} 与 {1:F6} 不符），z1/z2 的字段语义可能与假设不同",
                    radius,
                    endRadius);
                return false;
            }

            var center = new Point3D(centerX, centerY, DrawingPlaneZ);

            if (rotation < 0)
            {
                // 逆时针：起点角 → 起点角 + 扫掠角，正好是本类的表示
                arc = new TrajectorySegment
                {
                    SegType = SegmentType.Arc,
                    MotionMode = MotionMode.Dispense,
                    ArcCenter = center,
                    Radius = radius,
                    StartAngle = startAngle,
                    EndAngle = startAngle + sweep,
                    StartPt = new Point3D(item.x1, item.y1, DrawingPlaneZ),
                    EndPt = new Point3D(item.x2, item.y2, DrawingPlaneZ)
                };
            }
            else
            {
                // 顺时针：同一条弧换成逆时针表示，就是把起终点对调（圆心、半径、扫掠量都不变）
                arc = new TrajectorySegment
                {
                    SegType = SegmentType.Arc,
                    MotionMode = MotionMode.Dispense,
                    ArcCenter = center,
                    Radius = radius,
                    StartAngle = startAngle - sweep,
                    EndAngle = startAngle,
                    StartPt = new Point3D(item.x2, item.y2, DrawingPlaneZ),
                    EndPt = new Point3D(item.x1, item.y1, DrawingPlaneZ)
                };
            }

            reason = string.Empty;
            return true;
        }

        private static bool IsSamePlace(Point3D a, Point3D b) =>
            Math.Abs(a.X - b.X) <= ContinuityEpsilon && Math.Abs(a.Y - b.Y) <= ContinuityEpsilon;

        // ───────────────────────── 内部：调用与校验 ─────────────────────────

        private static void Check(int result, string what)
        {
            if (result != 0)
            {
                throw new InvalidOperationException(
                    $"{what} 失败，图形库错误码 {result}（含义查 ZMotionCadEx 手册的错误码表）");
            }
        }

        private void EnsureConnected()
        {
            if (Handle == IntPtr.Zero)
            {
                throw new InvalidOperationException(
                    "CAD 图形库未连接：先 ConnectEther(卡地址)；样例要求先连接再导入（见 ZCadFn.ZcadReadFile）");
            }
        }

        // ───────────────────────── 离线自检 ─────────────────────────

        /// <summary>
        /// 离线自检：不需要卡、不需要 <c>ZmotionCadEx.dll</c>，验证"图元数组 → 轨迹段"的全部规则。
        /// </summary>
        /// <returns>失败项清单；空列表表示全部通过</returns>
        /// <remarks>
        /// <para>
        /// 存在意义是：这套转换的规则（曲线边界、空移补齐、断开处不许连线、圆弧方向与字段自洽性）
        /// 全都是"错了也不报错、只是胶路悄悄变了"的类型，必须有能离线跑的检查钉住。
        /// 原生库只有 x64 且要卡在场，所以几何重建这一段刻意设计成纯函数
        /// （<see cref="Convert"/>），自检只喂托管结构体。
        /// </para>
        /// <para>
        /// 跑法：任何 net8.0 控制台工程引用本文件 + <c>Motion</c> / <c>Core.Motion</c> 的模型文件，
        /// 打印 <c>CadParser.SelfCheck()</c> 即可；返回非空就是回归。
        /// </para>
        /// </remarks>
        public static IReadOnlyList<string> SelfCheck()
        {
            var failures = new List<string>();

            void Expect(bool ok, string what)
            {
                if (!ok)
                {
                    failures.Add(what);
                }
            }

            // 0) 原生结构体的内存布局：图形库按"指向首元素的指针 + 数量"整块填数组
            //    （见 GetAllEntry），字段偏移对不上就是"读出来是垃圾坐标"这种最难查的故障。
            //    这里钉住的是手册结构体的自然布局，与样例 WindowsFormsApp1 的 ZmotionCadEx.cs 逐字段一致。
            Expect(
                Marshal.SizeOf<ZmotionCad.Struct_ZCad_Array>() == 104,
                $"图元结构体应占 104 字节（9 个 int + 4 字节填充 + 8 个 double），实际 {Marshal.SizeOf<ZmotionCad.Struct_ZCad_Array>()}");

            void ExpectOffset(string field, int expected)
            {
                int actual = Marshal.OffsetOf<ZmotionCad.Struct_ZCad_Array>(field).ToInt32();
                Expect(actual == expected, $"图元结构体字段 {field} 的偏移应为 {expected} 字节，实际 {actual}");
            }

            ExpectOffset(nameof(ZmotionCad.Struct_ZCad_Array.m_nItemtype), 4);
            ExpectOffset(nameof(ZmotionCad.Struct_ZCad_Array.x1), 40);
            ExpectOffset(nameof(ZmotionCad.Struct_ZCad_Array.z1), 56);
            ExpectOffset(nameof(ZmotionCad.Struct_ZCad_Array.x2), 64);
            ExpectOffset(nameof(ZmotionCad.Struct_ZCad_Array.z2), 80);
            ExpectOffset(nameof(ZmotionCad.Struct_ZCad_Array.m_dGetStartX), 88);

            // 1) 两条互不相连的直线图元：每条曲线前一条空移，段序 空移/点胶 交替
            CadParseResult twoCurves = Convert(new[]
            {
                MakeLine(0, 0, 10, 0, isCurveStart: true),
                MakeLine(20, 0, 30, 0, isCurveStart: true)
            });

            Expect(twoCurves.Warnings.Count == 0, $"纯直线图元不该有告警：{string.Join(" | ", twoCurves.Warnings)}");
            Expect(twoCurves.Segments.Count == 4, $"两条曲线应为 空移+点胶 各两条 = 4 段，实际 {twoCurves.Segments.Count}");

            if (twoCurves.Segments.Count == 4)
            {
                // 第一条曲线的空移起点未知，取曲线起点（长度 0）
                Expect(
                    twoCurves.Segments[0].MotionMode == MotionMode.Move &&
                    SamePoint(twoCurves.Segments[0].StartPt, new Point3D(0, 0, DrawingPlaneZ)) &&
                    SamePoint(twoCurves.Segments[0].EndPt, new Point3D(0, 0, DrawingPlaneZ)),
                    "首段应是一条长度 0 的空移（机器起始位置未知）");
                Expect(
                    twoCurves.Segments[1].MotionMode == MotionMode.Dispense &&
                    SamePoint(twoCurves.Segments[1].EndPt, new Point3D(10, 0, DrawingPlaneZ)),
                    "第 2 段应是第一条点胶直线");
                Expect(
                    twoCurves.Segments[2].MotionMode == MotionMode.Move &&
                    SamePoint(twoCurves.Segments[2].StartPt, new Point3D(10, 0, DrawingPlaneZ)) &&
                    SamePoint(twoCurves.Segments[2].EndPt, new Point3D(20, 0, DrawingPlaneZ)),
                    "第 3 段应是从上一条终点到第二条起点的空移");
                Expect(
                    SamePoint(twoCurves.Segments[3].EndPt, new Point3D(30, 0, DrawingPlaneZ)),
                    "第 4 段应是第二条点胶直线");
            }

            ExpectContinuous(twoCurves, "两条分开的曲线", Expect);

            // 2) 同一条曲线内首尾相接的两段直线：中间不该多出空移
            CadParseResult oneCurve = Convert(new[]
            {
                MakeLine(0, 0, 10, 0, isCurveStart: true),
                MakeLine(10, 0, 10, 5, isCurveStart: false)
            });
            Expect(oneCurve.Segments.Count == 3, $"一条曲线两段应为 空移+点胶×2 = 3 段，实际 {oneCurve.Segments.Count}");
            ExpectContinuous(oneCurve, "一条连续曲线", Expect);

            // 3) 只选中一部分：断开处必须落空移，而不是拉一条图纸上没有的直线
            CadParseResult partial = Convert(
                new[]
                {
                    MakeLine(0, 0, 10, 0, isCurveStart: true, selected: true),
                    MakeLine(10, 0, 20, 0, isCurveStart: false, selected: false),
                    MakeLine(20, 0, 30, 0, isCurveStart: false, selected: true)
                },
                new CadParseOptions { OnlySelected = true });
            Expect(partial.Segments.Count == 4, $"选中两段不连续的线应为 空移+点胶 ×2 = 4 段，实际 {partial.Segments.Count}");
            ExpectContinuous(partial, "只选一部分图形", Expect);

            // 4) 逆时针圆弧：起点 (15,10)、起始角 0、扫掠 +90°（z2 = -π/2），终点 (10,15)
            const double r = 5;
            CadParseResult ccw = Convert(new[]
            {
                MakeArc(15, 10, 10, 15, startAngle: 0, rotation: -Math.PI / 2, isCurveStart: true)
            });
            Expect(ccw.Segments.Count == 2, $"一条圆弧曲线应为 空移+圆弧 = 2 段，实际 {ccw.Segments.Count}");
            if (ccw.Segments.Count == 2)
            {
                TrajectorySegment arc = ccw.Segments[1];
                Expect(arc.SegType == SegmentType.Arc, "逆时针圆弧段类型应为 Arc");
                Expect(SamePoint(arc.ArcCenter, new Point3D(10, 10, DrawingPlaneZ)), $"圆心应为 (10,10)，实际 {arc.ArcCenter}");
                Expect(Math.Abs(arc.Radius - r) < 1e-9, $"半径应为 {r}，实际 {arc.Radius}");
                Expect(SamePoint(arc.StartPt, new Point3D(15, 10, DrawingPlaneZ)), $"起点应为 (15,10)，实际 {arc.StartPt}");
                Expect(SamePoint(arc.EndPt, new Point3D(10, 15, DrawingPlaneZ)), $"终点应为 (10,15)，实际 {arc.EndPt}");
                Expect(Math.Abs(arc.CircleSweep - Math.PI / 2) < 1e-9, $"扫掠角应为 90°，实际 {arc.CircleSweep}");
            }

            // 5) 顺时针圆弧：同样那段弧反过来走（z2 = +π/2，起点 (10,15)、起始角 π/2、终点 (15,10)），
            //    本类统一按逆时针表示，所以起终点要对调，扫掠角仍为 90°
            CadParseResult cw = Convert(new[]
            {
                MakeArc(10, 15, 15, 10, startAngle: Math.PI / 2, rotation: Math.PI / 2, isCurveStart: true)
            });
            if (cw.Segments.Count == 2)
            {
                TrajectorySegment arc = cw.Segments[1];
                Expect(SamePoint(arc.StartPt, new Point3D(15, 10, DrawingPlaneZ)), $"顺时针弧对调后起点应为 (15,10)，实际 {arc.StartPt}");
                Expect(SamePoint(arc.EndPt, new Point3D(10, 15, DrawingPlaneZ)), $"顺时针弧对调后终点应为 (10,15)，实际 {arc.EndPt}");
                Expect(Math.Abs(arc.CircleSweep - Math.PI / 2) < 1e-9, $"顺时针弧的逆时针表示扫掠角应为 90°，实际 {arc.CircleSweep}");
            }
            else
            {
                failures.Add($"顺时针圆弧曲线应为 2 段，实际 {cw.Segments.Count}");
            }

            // 6) 字段不自洽（终点不在重建出的圆上）：必须告警并退化成弦线，不许静默当圆弧
            CadParseResult inconsistent = Convert(new[]
            {
                MakeArc(15, 10, 12, 9, startAngle: 0, rotation: -Math.PI / 2, isCurveStart: true)
            });
            Expect(inconsistent.Warnings.Count == 1, $"不自洽的圆弧应产生 1 条告警，实际 {inconsistent.Warnings.Count}");
            if (inconsistent.Segments.Count == 2)
            {
                Expect(inconsistent.Segments[1].SegType == SegmentType.Line, "不自洽的圆弧应退化成直线段");
            }
            else
            {
                failures.Add($"不自洽圆弧的曲线应为 2 段，实际 {inconsistent.Segments.Count}");
            }

            // 7) 不支持的图元类型：整条跳过，但告警一次（不是一万次）
            CadParseResult unsupported = Convert(new[]
            {
                MakeLine(0, 0, 1, 0, isCurveStart: true),
                MakeRaw(itemType: 2005, isCurveStart: true),
                MakeRaw(itemType: 2005, isCurveStart: true),
                MakeRaw(itemType: 2007, isCurveStart: true)
            });
            Expect(unsupported.Warnings.Count == 2, $"两种不支持的图元应各告警一次（共 2 条），实际 {unsupported.Warnings.Count}");
            Expect(unsupported.Segments.Count == 2, $"不支持的图元不该产生轨迹段，实际 {unsupported.Segments.Count} 段");

            // 8) 单独点：不产生段，但要让现场知道漏了点
            CadParseResult dots = Convert(new[] { MakeRaw(itemType: 2001) });
            Expect(dots.Segments.Count == 0, $"单独点不该产生轨迹段，实际 {dots.Segments.Count}");
            Expect(dots.Warnings.Count == 1, $"单独点应产生 1 条告警，实际 {dots.Warnings.Count}");

            // 9) 端到端：转换结果必须被配方接受——相邻段首尾相接是卡侧下发的硬前提，
            //    否则卡会在两段之间自己拉一条直线（撞针）。这条检查把"导图"和"下发"接起来。
            var program = new DispenseProgram("CadParser.SelfCheck");
            try
            {
                program.LoadSegments(twoCurves.Segments);
                program.BuildMotionCardCommands();
            }
            catch (Exception ex)
            {
                failures.Add($"转换结果被 DispenseProgram 拒绝（相邻段不连续一类）：{ex.Message}");
            }

            return failures;
        }

        private static bool SamePoint(Point3D a, Point3D b) => IsSamePlace(a, b);

        /// <summary>自检用：整条轨迹必须首尾相接（索引顺序就是执行顺序）</summary>
        private static void ExpectContinuous(CadParseResult result, string what, Action<bool, string> expect)
        {
            for (int i = 1; i < result.Segments.Count; i++)
            {
                TrajectorySegment previous = result.Segments[i - 1];
                TrajectorySegment current = result.Segments[i];

                if (!IsSamePlace(previous.EndPt, current.StartPt))
                {
                    expect(false, $"{what}：第 {i - 1} 段终点 {previous.EndPt} 与第 {i} 段起点 {current.StartPt} 不连续");
                    return;
                }
            }
        }

        /// <summary>自检用：造一个直线图元</summary>
        private static ZmotionCad.Struct_ZCad_Array MakeLine(
            double x1, double y1, double x2, double y2, bool isCurveStart, bool selected = true) =>
            new ZmotionCad.Struct_ZCad_Array
            {
                m_nItemtype = ZmotionCad.ZCAD_ITEMTYPE_VECTLine,
                m_nInVectFrist = isCurveStart ? 1 : 0,
                m_nChoose = selected ? 1 : 0,
                m_dGetStartX = x1,
                m_dGetStartY = y1,
                x1 = x1,
                y1 = y1,
                x2 = x2,
                y2 = y2
            };

        /// <summary>自检用：造一个圆弧图元</summary>
        private static ZmotionCad.Struct_ZCad_Array MakeArc(
            double x1, double y1, double x2, double y2, double startAngle, double rotation, bool isCurveStart) =>
            new ZmotionCad.Struct_ZCad_Array
            {
                m_nItemtype = ZmotionCad.ZCAD_ITEMTYPE_VECTArc,
                m_nInVectFrist = isCurveStart ? 1 : 0,
                m_nChoose = 1,
                m_dGetStartX = x1,
                m_dGetStartY = y1,
                x1 = x1,
                y1 = y1,
                z1 = startAngle,
                x2 = x2,
                y2 = y2,
                z2 = rotation
            };

        /// <summary>自检用：造一个只有类型的图元（点 / 样条 / 文本）</summary>
        private static ZmotionCad.Struct_ZCad_Array MakeRaw(int itemType, bool isCurveStart = false) =>
            new ZmotionCad.Struct_ZCad_Array
            {
                m_nItemtype = itemType,
                m_nInVectFrist = isCurveStart ? 1 : 0,
                m_nChoose = 1
            };
    }
}
