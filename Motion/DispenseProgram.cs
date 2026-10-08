using Core.Motion;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Motion
{
    /// <summary>
    /// 点胶配方（一套产品程序）：整套轨迹 + 基准 Mark + 产品信息
    /// </summary>
    /// <remarks>
    /// 本类只承载配方数据与几何变换，<b>不解析 DXF</b>。
    /// DXF 导入是外部导入源的事，由 Plugins 里的解析器产出 <see cref="TrajectorySegment"/> 后
    /// 经 <see cref="LoadSegments"/> 灌进来，避免运动模块反向依赖解析插件。
    /// </remarks>
    public class DispenseProgram
    {
        public DispenseProgram()
        {
        }

        public DispenseProgram(string programName)
        {
            ProgramName = programName;
        }

        /// <summary>配方名称</summary>
        public string ProgramName = string.Empty;

        /// <summary>基准 Mark 点，手眼标定后用于视觉匹配工件</summary>
        public List<Point3D> BaseMarks = new List<Point3D>();

        /// <summary>
        /// 轨迹段列表：List 索引顺序 = 设备执行顺序。
        /// DXF 导入生成初始顺序，拖拽编辑器修改索引。
        /// </summary>
        public List<TrajectorySegment> Segments = new List<TrajectorySegment>();

        /// <summary>
        /// 深拷贝整份配方。位姿补偿会变换段几何，基准配方必须留在原位不被动到，
        /// 所以每颗产品的变换都作用在副本上。
        /// </summary>
        public DispenseProgram Clone()
        {
            var copy = new DispenseProgram(ProgramName)
            {
                BaseMarks = new List<Point3D>(BaseMarks),
                Segments = new List<TrajectorySegment>(Segments.Count)
            };

            foreach (TrajectorySegment seg in Segments)
            {
                copy.Segments.Add(seg.Clone());
            }

            return copy;
        }

        /// <summary>
        /// 接收外部解析好的轨迹段（DXF / 配方文件 / 编辑器），并统一补齐段长。
        /// </summary>
        /// <param name="segments">解析侧产出的轨迹段，按加工顺序排列</param>
        /// <param name="programName">配方名，留空则保留原名</param>
        /// <remarks>
        /// 段长是运行期需要的派生量，解析侧只管几何，统一在这里补齐，避免两处各算一遍。
        /// </remarks>
        public void LoadSegments(IEnumerable<TrajectorySegment> segments, string? programName = null)
        {
            if (segments == null)
            {
                throw new ArgumentNullException(nameof(segments));
            }

            var accepted = new List<TrajectorySegment>();
            foreach (TrajectorySegment seg in segments)
            {
                if (seg == null)
                {
                    continue;
                }

                seg.CalcSegmentLength();
                accepted.Add(seg);
            }

            Segments = accepted;

            if (!string.IsNullOrWhiteSpace(programName))
            {
                ProgramName = programName;
            }
        }

        /// <summary>
        /// 整条轨迹批量坐标变换（上位机预变换方案备用）
        /// </summary>
        /// <param name="dx">X 方向平移量 mm</param>
        /// <param name="dy">Y 方向平移量 mm</param>
        /// <param name="theta">绕 Z 轴旋转角，弧度</param>
        /// <param name="rotCenter">旋转中心</param>
        /// <remarks>
        /// 推荐方案不使用本方法：基准轨迹预下载到运动卡，由卡内部实时变换。
        /// 这里供离线预览、无卡调试与导出静态轨迹时使用。
        /// </remarks>
        public void ApplyPoseOffset(double dx, double dy, double theta, Point3D rotCenter)
        {
            foreach (TrajectorySegment seg in Segments)
            {
                seg.Transform(dx, dy, theta, rotCenter);
            }
        }

        /// <summary>
        /// 保存配方文件（JSON）。
        /// </summary>
        /// <param name="path">目标文件路径，目录不存在会自动创建</param>
        /// <returns>是否保存成功</returns>
        /// <remarks>
        /// <para>
        /// 只保存基准轨迹的几何、工艺与 IO 触发，不含任何位姿补偿结果——补偿是逐颗产品的运行期状态，
        /// 写进配方会让下一颗产品带着上一颗的偏移开工。
        /// </para>
        /// <para>
        /// <see cref="TrajectorySegment.SegmentLength"/> 是派生量，<b>不落盘</b>，
        /// 加载时由 <see cref="TrajectorySegment.CalcSegmentLength"/> 重算，避免文件里的旧值与几何不一致。
        /// </para>
        /// <para>
        /// 先写临时文件再替换，避免写到一半失败把原有配方截断成半截 JSON。
        /// </para>
        /// </remarks>
        public bool SaveRecipe(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            try
            {
                string fullPath = Path.GetFullPath(path);
                string? directory = Path.GetDirectoryName(fullPath);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                var file = new RecipeFile
                {
                    ProgramName = ProgramName,
                    BaseMarks = new List<Point3D>(BaseMarks),
                    Segments = Segments.Select(RecipeSegment.From).ToList()
                };

                // 先写 .tmp：序列化或磁盘写失败时，原配方文件仍然完整
                string tempPath = fullPath + ".tmp";
                File.WriteAllText(tempPath, JsonSerializer.Serialize(file, JsonOptions), Encoding.UTF8);
                File.Move(tempPath, fullPath, overwrite: true);

                return true;
            }
            catch (Exception)
            {
                // 返回 false 而不是抛异常：调用方（配方编辑器 / 配方管理）需要能提示"保存失败"并继续，
                // 详细的 IO / 序列化异常属于上层日志职责，这里不吞掉信息量的做法是保留 false 语义
                return false;
            }
        }

        /// <summary>
        /// 从配方 JSON 构建一份<b>新的</b>配方对象（不含位姿补偿，段长按几何重算）。
        /// </summary>
        /// <param name="path">配方文件路径</param>
        /// <param name="program">读出的配方；失败时为 null</param>
        /// <returns>是否读取成功</returns>
        /// <remarks>
        /// 只读不写，不触碰当前实例：本方法是 <see cref="LoadRecipe"/> 的"构建"阶段，
        /// 由它成功后再由 <see cref="LoadRecipe"/> 提交到自身，从而保证失败不脏实例。
        /// 对外入口只有 <see cref="LoadRecipe"/> 与 <see cref="SaveRecipe"/>，本方法保持私有。
        /// </remarks>
        private static bool TryReadRecipe(string path, out DispenseProgram? program)
        {
            program = null;

            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            try
            {
                if (!File.Exists(path))
                {
                    return false;
                }

                string json = File.ReadAllText(path, Encoding.UTF8);
                var file = JsonSerializer.Deserialize<RecipeFile>(json, JsonOptions);
                if (file == null)
                {
                    return false;
                }

                var loaded = new DispenseProgram(file.ProgramName ?? string.Empty)
                {
                    BaseMarks = file.BaseMarks ?? new List<Point3D>()
                };

                // 段长按几何重算，不信任文件里的值
                loaded.LoadSegments((file.Segments ?? new List<RecipeSegment>())
                    .Where(static seg => seg != null)
                    .Select(static seg => seg!.ToSegment()));

                program = loaded;
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// 从配方文件加载并<b>覆盖</b>当前实例的内容。
        /// </summary>
        /// <param name="path">配方文件路径（<see cref="SaveRecipe"/> 写出的 JSON）</param>
        /// <returns>是否加载成功；失败时当前实例保持不变</returns>
        /// <remarks>
        /// <para>
        /// 语义是「读入并覆盖自己」，不是 DXF 导入：本类不认识 DXF，
        /// DXF 到轨迹段的转换由导入侧的解析器负责（见 <see cref="LoadSegments"/>）。
        /// </para>
        /// <para>
        /// 解析全部委托给 <see cref="TryReadRecipe"/>，它先把新配方完整构建出来；
        /// 只有构建成功才提交到自身，因此文件损坏时不会把当前实例改成半套轨迹。
        /// </para>
        /// </remarks>
        public bool LoadRecipe(string path)
        {
            if (!TryReadRecipe(path, out DispenseProgram? loaded) || loaded == null)
            {
                // 读失败不动当前实例，调用方可以继续用旧配方
                return false;
            }

            // 提交阶段：到这里所有解析与段长计算都已完成，不会再失败
            ProgramName = loaded.ProgramName;
            BaseMarks = loaded.BaseMarks;
            Segments = loaded.Segments;

            return true;
        }

        /// <summary>
        /// 组装下发到运动控制卡的指令集：<b>路径指令与 IO 指令分成两个列表</b>。
        /// </summary>
        /// <param name="dioBitNo">胶阀输出位号（取决于现场接线，默认 0）</param>
        /// <returns>指令集</returns>
        /// <exception cref="InvalidOperationException">
        /// 轨迹为空，或存在无法下发的段（速度非法、IO 触发越界、圆弧几何非法、
        /// 整圆未拆分、相邻段端点不连续）。
        /// </exception>
        /// <remarks>
        /// <para>
        /// <b>为什么不把 IO 塞进路径点里：</b>路径点是纯坐标，放不下"这里要开阀"，
        /// 用哨兵值编码又不可读。<see cref="IoTrigger.DistanceOnSegment"/> 本来就是
        /// "本段起点起多少 mm"的相对行程，各品牌的转换器都能把它落成
        /// "按段内行程触发的输出"（位置比较输出 / 位置同步输出一类），
        /// 所以 IO 独立成表、按段号与行程距离表达，卡侧按行程触发即可。
        /// </para>
        /// <para>
        /// <b>阀门物理提前量不在这里算。</b>开阀响应时间（如 0.1s）是设备属性，
        /// 不在配方里；提前量属于各品牌转换器 / 卡侧参数的事，
        /// 本方法只输出配方里明确写了的东西。
        /// </para>
        /// <para>
        /// <b>圆弧不离散。</b>圆弧段输出为保留圆心与半径的圆弧指令，
        /// 由转换器落到该品牌的原生圆弧插补，保住设计文档方案 A 的原生圆弧插补。
        /// </para>
        /// </remarks>
        public MotionCardCommandSet BuildMotionCardCommands(ushort dioBitNo = 0)
        {
            if (Segments.Count == 0)
            {
                throw new InvalidOperationException("轨迹为空，没有可下发的段。");
            }

            var path = new List<MotionCardCommand>(Segments.Count);
            var io = new List<MotionCardIoCommand>();
            var current = new Point3D(double.NaN, double.NaN, double.NaN);

            for (int i = 0; i < Segments.Count; i++)
            {
                TrajectorySegment seg = Segments[i];
                string? error = ValidateForCard(seg);
                if (error != null)
                {
                    throw new InvalidOperationException($"第 {i} 段无法下发：{error}");
                }

                // 相邻段端点必须接得上，否则卡会在两段之间自己拉一条直线（撞针）
                if (i > 0 && !IsSamePlace(current, seg.StartPt))
                {
                    throw new InvalidOperationException(
                        $"第 {i} 段起点 {seg.StartPt} 与上一段终点 {current} 不连续，" +
                        "直接下发会让卡在两段之间拉一条直线。");
                }

                if (seg.SegType == SegmentType.Arc)
                {
                    path.Add(new MotionCardArcCommand
                    {
                        Mark = i,
                        Target = ToArray(seg.EndPt),
                        Center = ToArray(seg.ArcCenter),
                        Radius = seg.Radius,
                        // 约定：0=顺时针 / 1=逆时针。方向是卡的约定值，现场首次联调需确认
                        ArcDirection = ArcDirectionCcw,
                        CircleCount = 0,
                        Speed = seg.Speed,
                        Mode = ToCardMode(seg.MotionMode),
                        Z = ToAxisZ(seg)
                    });
                }
                else
                {
                    path.Add(new MotionCardLineCommand
                    {
                        Mark = i,
                        Target = ToArray(seg.EndPt),
                        Speed = seg.Speed,
                        Mode = ToCardMode(seg.MotionMode),
                        Z = ToAxisZ(seg)
                    });
                }

                // IO 按"当前段号 + 段内行程"表达，卡按行程触发，与是否被位姿补偿无关
                foreach (IoTrigger trigger in seg.IoTriggers)
                {
                    if (trigger == null)
                    {
                        continue;
                    }

                    io.Add(new MotionCardIoCommand
                    {
                        Mark = i,
                        BitNo = dioBitNo,
                        On = trigger.Action == IoActionType.ValveOn,
                        DistanceOnSegment = trigger.DistanceOnSegment,
                        Action = trigger.Action,
                        DelayMs = trigger.DelayMs
                    });
                }

                current = seg.EndPt;
            }

            return new MotionCardCommandSet(path, io);
        }

        /// <summary>顺时针圆弧方向约定值</summary>
        private const ushort ArcDirectionCw = 0;

        /// <summary>逆时针圆弧方向约定值</summary>
        private const ushort ArcDirectionCcw = 1;

        /// <summary>坐标连续性判定阈值（mm），只用于吸收浮点误差</summary>
        private const double ContinuityEpsilon = 1e-6;

        /// <summary>卡侧路径模式：空移</summary>
        private const ushort CardModeMove = 0;

        /// <summary>卡侧路径模式：点胶（出胶）</summary>
        private const ushort CardModeDispense = 1;

        /// <summary>目标点按 X / Y 两个轴下发</summary>
        private static double[] ToArray(Point3D p) => new[] { p.X, p.Y };

        /// <summary>本段该用工作高度还是抬刀高度，由运动模式决定</summary>
        private static double ToAxisZ(TrajectorySegment seg) =>
            seg.MotionMode == MotionMode.Move ? seg.LiftZ : seg.WorkZ;

        private static ushort ToCardMode(MotionMode mode) =>
            mode == MotionMode.Move ? CardModeMove : CardModeDispense;

        private static bool IsSamePlace(Point3D a, Point3D b) =>
            Math.Abs(a.X - b.X) <= ContinuityEpsilon &&
            Math.Abs(a.Y - b.Y) <= ContinuityEpsilon;

        /// <summary>
        /// 下发前的单段校验。段长会在缺失时按几何补算，保证 IO 触发距离有校验依据。
        /// </summary>
        /// <param name="seg">轨迹段</param>
        /// <returns>成功返回 null，失败返回原因</returns>
        private static string? ValidateForCard(TrajectorySegment seg)
        {
            if (seg == null)
            {
                return "段为 null。";
            }

            if (double.IsNaN(seg.Speed) || double.IsInfinity(seg.Speed) || seg.Speed <= 0)
            {
                return $"速度非法（{seg.Speed}），卡无法规划。";
            }

            if (seg.SegmentLength <= 0)
            {
                seg.CalcSegmentLength();
            }

            double length = seg.SegmentLength;

            foreach (IoTrigger trigger in seg.IoTriggers)
            {
                if (trigger == null)
                {
                    continue;
                }

                if (double.IsNaN(trigger.DistanceOnSegment) ||
                    trigger.DistanceOnSegment < 0 ||
                    trigger.DistanceOnSegment > length)
                {
                    return $"IO 触发距离 {trigger.DistanceOnSegment} 超出自段长度 {length:F4}，" +
                           "卡按段内行程触发时会永远等不到。";
                }
            }

            if (seg.SegType != SegmentType.Arc)
            {
                return null;
            }

            if (double.IsNaN(seg.Radius) || seg.Radius <= 0)
            {
                return $"圆弧半径非法（{seg.Radius}），卡无法做圆弧插补。";
            }

            double centerToStart = Math.Sqrt(
                (seg.StartPt.X - seg.ArcCenter.X) * (seg.StartPt.X - seg.ArcCenter.X) +
                (seg.StartPt.Y - seg.ArcCenter.Y) * (seg.StartPt.Y - seg.ArcCenter.Y));
            if (double.IsNaN(centerToStart) || centerToStart <= 0)
            {
                return "圆弧圆心与起点重合，几何非法。";
            }

            double sweep = seg.CircleSweep;
            if (sweep <= 0 || sweep >= 2 * Math.PI - 1e-9)
            {
                return "整圆无法用单段圆弧下发（终点与起点重合），" +
                       "请先用 TrajectorySegment.FromCircle 拆成两段半圆弧。";
            }

            return null;
        }

        /// <summary>配方文件当前版本号</summary>
        private const int CurrentSchemaVersion = 1;

        /// <summary>
        /// 序列化选项：几何全是公开字段（<see cref="Point3D"/> 就是字段版结构体），
        /// 不打开 <c>IncludeFields</c> 会把这些字段全部序列化成空对象。
        /// </summary>
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            IncludeFields = true,
            PropertyNameCaseInsensitive = true,
            WriteIndented = true,
            // 与仓库其它配置文件（algorithm-profiles.json）统一用 camelCase
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            // 枚举写成名字（"Arc"/"Dispense"）而不是数字，配方文件要能人工审阅、手工改
            Converters = { new JsonStringEnumConverter() }
        };

        /// <summary>配方文件根对象，字段顺序即落盘顺序</summary>
        private sealed class RecipeFile
        {
            /// <summary>schema 版本，用于后续兼容旧配方</summary>
            public int SchemaVersion { get; set; } = CurrentSchemaVersion;

            public string? ProgramName { get; set; }

            public List<Point3D>? BaseMarks { get; set; }

            public List<RecipeSegment>? Segments { get; set; }
        }

        /// <summary>
        /// 配方文件里的单个轨迹段。
        /// </summary>
        /// <remarks>
        /// 单独定义而不是直接序列化 <see cref="TrajectorySegment"/>，是为了精确控制落盘内容：
        /// 排除派生量 <see cref="TrajectorySegment.SegmentLength"/>，避免文件里留下与几何不一致的旧值。
        /// </remarks>
        private sealed class RecipeSegment
        {
            public SegmentType SegType { get; set; }

            public MotionMode MotionMode { get; set; }

            public Point3D StartPt { get; set; }

            public Point3D EndPt { get; set; }

            public Point3D ArcCenter { get; set; }

            public double Radius { get; set; }

            public double StartAngle { get; set; }

            public double EndAngle { get; set; }

            public double Speed { get; set; }

            public double WorkZ { get; set; }

            public double LiftZ { get; set; }

            public List<IoTrigger>? IoTriggers { get; set; }

            /// <summary>由内存模型生成落盘对象</summary>
            public static RecipeSegment From(TrajectorySegment seg) => new()
            {
                SegType = seg.SegType,
                MotionMode = seg.MotionMode,
                StartPt = seg.StartPt,
                EndPt = seg.EndPt,
                ArcCenter = seg.ArcCenter,
                Radius = seg.Radius,
                StartAngle = seg.StartAngle,
                EndAngle = seg.EndAngle,
                Speed = seg.Speed,
                WorkZ = seg.WorkZ,
                LiftZ = seg.LiftZ,
                IoTriggers = new List<IoTrigger>(seg.IoTriggers)
            };

            /// <summary>还原成内存模型，段长交给调用方重算</summary>
            public TrajectorySegment ToSegment() => new()
            {
                SegType = SegType,
                MotionMode = MotionMode,
                StartPt = StartPt,
                EndPt = EndPt,
                ArcCenter = ArcCenter,
                Radius = Radius,
                StartAngle = StartAngle,
                EndAngle = EndAngle,
                Speed = Speed,
                WorkZ = WorkZ,
                LiftZ = LiftZ,
                IoTriggers = IoTriggers == null ? new List<IoTrigger>() : new List<IoTrigger>(IoTriggers)
            };
        }

   
    }

    /// <summary>
    /// 下发给运动控制卡的一条路径指令（几何与目标点）。
    /// </summary>
    /// <remarks>
    /// 用 <c>init</c> 属性而不是位置记录参数：几何参数由基类持有、圆弧额外参数由派生类持有，
    /// 位置参数无法在对象初始化器里给派生类型赋值。
    /// </remarks>
    public abstract record MotionCardCommand
    {
        /// <summary>指令序号，等于配方里段的下标，用于对位与产线追溯</summary>
        public int Mark { get; init; }

        /// <summary>目标点，按轴序排列（当前为 X, Y 绝对坐标 mm）</summary>
        public double[] Target { get; init; } = Array.Empty<double>();

        /// <summary>本段速度 mm/s</summary>
        public double Speed { get; init; }

        /// <summary>卡侧运动模式：0=空移，1=点胶</summary>
        public ushort Mode { get; init; }

        /// <summary>本段该用的高度：点胶取 WorkZ，空移取 LiftZ</summary>
        public double Z { get; init; }
    }

    /// <summary>
    /// 直线指令；转换器把它落成该品牌的直线插补（如正运动的 MOVEA 一类）。
    /// </summary>
    public sealed record MotionCardLineCommand : MotionCardCommand;

    /// <summary>
    /// 圆弧指令：由"终点 + 圆心"定义，转换器落到该品牌的原生圆弧插补。
    /// </summary>
    /// <remarks>
    /// 圆弧保留原生圆心 / 半径，不做离散，保住卡的圆弧插补能力。
    /// 整圆已在下发前被拒绝（终点与起点重合，卡分不清走整圈还是不走），
    /// 所以 <see cref="CircleCount"/> 恒为 0。
    /// </remarks>
    public sealed record MotionCardArcCommand : MotionCardCommand
    {
        /// <summary>圆心（X, Y）</summary>
        public double[] Center { get; init; } = Array.Empty<double>();

        /// <summary>半径 mm，仅作核对，卡侧用圆心时不需要</summary>
        public double Radius { get; init; }

        /// <summary>圆弧方向：0=顺时针，1=逆时针（卡侧约定值，首次联调需确认）</summary>
        public ushort ArcDirection { get; init; }

        /// <summary>整圈数，单段圆弧为 0（卡侧参数语义，首次联调需确认）</summary>
        public int CircleCount { get; init; }
    }

    /// <summary>
    /// 下发给运动控制卡的一条 IO 指令。
    /// </summary>
    public sealed record MotionCardIoCommand
    {
        /// <summary>所属段的下标，与路径指令的 <see cref="MotionCardCommand.Mark"/> 对应</summary>
        public int Mark { get; init; }

        /// <summary>输出位号（胶阀），取决于现场接线</summary>
        public ushort BitNo { get; init; }

        /// <summary>true=开（ValveOn），false=关（ValveOff）</summary>
        public bool On { get; init; }

        /// <summary>
        /// 自所属段起点起的行进距离 mm。转换器把它交给该品牌的
        /// "按行程触发输出"（位置比较输出 / 位置同步输出 PSO 一类）；
        /// 段内相对行程与位姿补偿无关，所以补偿后无需改这里。
        /// </summary>
        public double DistanceOnSegment { get; init; }

        /// <summary>原始动作类型，保留以便卡侧区分 ValveOn / ValveOff / Delay</summary>
        public IoActionType Action { get; init; }

        /// <summary>延时毫秒，仅 Delay 类型有意义</summary>
        public double DelayMs { get; init; }
    }

    /// <summary>
    /// 一次下发的完整指令集：路径与 IO 分成两个列表。
    /// </summary>
    /// <param name="Path">路径指令，按加工顺序排列</param>
    /// <param name="Io">IO 指令，按所属段号与行程距离排列</param>
    /// <remarks>
    /// 之所以不把 IO 混进路径点：路径点是纯坐标，放不下开阀事件；
    /// 用哨兵值编码则不可读、不可校验。分开表达后，
    /// 路径每条指令对应卡的直线 / 圆弧指令，IO 走卡的行程触发指令。
    /// </remarks>
    public sealed record MotionCardCommandSet(
        IReadOnlyList<MotionCardCommand> Path, IReadOnlyList<MotionCardIoCommand> Io);
}
