using Core.Motion;
using System;
using System.Globalization;
using System.Text;

namespace Motion.Zmotion
{
    /// <summary>
    /// 转换器：厂商无关的指令集 → <b>正运动 ZBasic 程序</b>。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 正运动是"程序常驻卡上 + 变量传参数"，所以这里生成的程序里
    /// <b>带着位姿变换本身</b>——轨迹是基准轨迹，每颗产品的 dx/dy/θ 由上位机写 VR，
    /// 卡上循环自己做旋转矩阵、自己判软限位。这就是"下载一次、每次只传位姿"；
    /// 只能整包下发程序的卡做不到这一点，必须在位姿变化后重下。
    /// </para>
    /// <para>
    /// <b>骨架阶段的诚实边界</b>：
    /// <list type="bullet">
    /// <item>阀门时序（PSO / <c>HW_PSWITCH</c>）参数序尚未核实，所以只要轨迹里有 IO 事件，
    /// 生成的程序会<b>置错误码 9001 并拒绝运行</b>，而不是空跑一遍不出胶。</item>
    /// <item>连续插补、<c>UNITS</c>、<c>BASE</c> 的写法用 <c>MOVEA</c>/<c>MOVECIRC</c> 占位，
    /// 程序里逐处标了"待核"。</item>
    /// <item>圆弧段的段内 IO 拆分尚未实现（正运动能表达，属待实现）。</item>
    /// </list>
    /// </para>
    /// <para>纯函数、无状态，可安全并发调用。</para>
    /// </remarks>
    public sealed class ZmotionProgramWriter : IMotionProgramWriter
    {
        private readonly string programName;

        /// <param name="programName">控制器内的程序名（ASCII，避免编码问题）。</param>
        public ZmotionProgramWriter(string programName = "DISPENSE")
        {
            if (string.IsNullOrWhiteSpace(programName))
            {
                throw new ArgumentException("程序名不能为空", nameof(programName));
            }

            this.programName = programName;
        }

        /// <inheritdoc />
        public string FormatName => "Zmotion-ZBasic";

        /// <inheritdoc />
        public MotionProgramPayload Write(MotionCardCommandSet commands)
        {
            if (commands is null)
            {
                throw new ArgumentNullException(nameof(commands));
            }

            if (commands.Path.Count == 0)
            {
                throw new InvalidOperationException("轨迹为空，没有可下发的段。");
            }

            var text = new StringBuilder();
            AppendHeader(text);
            AppendDeclarations(text, commands);
            AppendSegmentTable(text, commands);
            AppendIoTable(text, commands);
            AppendMainLoop(text);

            return new MotionProgramPayload(programName, text.ToString());
        }

        /// <summary>程序头：把契约（VR 编号、表布局）写进程序本身，现场对着程序就能核。</summary>
        private static void AppendHeader(StringBuilder text)
        {
            text.AppendLine("' ==================================================================");
            text.AppendLine("' DispensVisTrace 自动生成 —— 请勿手改；要改请改 ZmotionProgramWriter");
            text.AppendLine("' 目标控制器：正运动 ZMC（ZBasic）");
            text.AppendLine("' 数据契约（与 ZmotionProgramContract 一一对应）：");
            text.AppendFormat(
                "'   VR：{0}=dx(mm) {1}=dy(mm) {2}=theta(rad) {3}=旋转中心X {4}=旋转中心Y{5}",
                ZmotionProgramContract.VrPoseDx,
                ZmotionProgramContract.VrPoseDy,
                ZmotionProgramContract.VrPoseTheta,
                ZmotionProgramContract.VrRotCenterX,
                ZmotionProgramContract.VrRotCenterY,
                Environment.NewLine);
            text.AppendFormat(
                "'   VR：{0}=运行请求(上位机置1) {1}=完成标志(卡置1) {2}=错误码{3}",
                ZmotionProgramContract.VrRunRequest,
                ZmotionProgramContract.VrDone,
                ZmotionProgramContract.VrError,
                Environment.NewLine);
            text.AppendFormat(
                "'   轨迹表 TABLE：0 起，每段 {0} 项 = 终点X, 终点Y, 速度, 模式(1=点胶), 圆心X, 圆心Y, 段类型(1=圆弧){1}",
                ZmotionProgramContract.TableStride,
                Environment.NewLine);
            text.AppendFormat(
                "'   IO 表 TABLE：段表之后，每事件 {0} 项 = 段号, 段内行程(mm), 开关(1=开阀){1}",
                ZmotionProgramContract.IoStride,
                Environment.NewLine);
            text.AppendLine("' 待核（首次联调逐条确认）：");
            text.AppendLine("'   1) PSO / HW_PSWITCH 参数序 —— 未核实前，有 IO 事件的轨迹会被本程序拒绝运行(9001)");
            text.AppendLine("'   2) 连续插补与缓冲指令名（本骨架用 MOVEA / MOVECIRC 占位）");
            text.AppendLine("'   3) UNITS 脉冲当量 与 BASE 轴映射");
            text.AppendLine("'   4) VR / TABLE 上限");
            text.AppendLine("' ==================================================================");
        }

        /// <summary>变量与常量声明。</summary>
        private static void AppendDeclarations(StringBuilder text, MotionCardCommandSet commands)
        {
            text.AppendLine();
            text.AppendLine("' ---- 变量与常量 ----");
            text.AppendLine("DIM i, segCount, ioCount AS INTEGER");
            text.AppendLine("DIM dx, dy, th, cx, cy, x, y, sp, m, st, tx, ty, ax, ay, acx, acy AS FLOAT");
            text.AppendLine();

            // 待核：BASE / UNITS 的具体写法按手册；轴映射必须与电气一致
            text.AppendLine("' 待核：轴映射与脉冲当量");
            text.AppendLine("BASE(0, 1)                    ' 轴0 = X，轴1 = Y");
            text.AppendLine("UNITS = 100                   ' 待核：脉冲当量");
            text.AppendLine();

            text.AppendFormat(
                CultureInfo.InvariantCulture,
                "segCount = {0}{1}",
                commands.Path.Count,
                Environment.NewLine);
            text.AppendFormat(
                CultureInfo.InvariantCulture,
                "ioCount = {0}{1}",
                commands.Io.Count,
                Environment.NewLine);
        }

        /// <summary>轨迹表：每段 7 项（见 <see cref="ZmotionProgramContract"/>）。</summary>
        private static void AppendSegmentTable(StringBuilder text, MotionCardCommandSet commands)
        {
            text.AppendLine();
            text.AppendLine("' ---- 轨迹表（工件坐标，未含位姿）----");

            for (int i = 0; i < commands.Path.Count; i++)
            {
                MotionCardCommand command = commands.Path[i];

                if (command.Target is null || command.Target.Length < 2)
                {
                    throw new InvalidOperationException(
                        $"第 {command.Mark} 段的 Target 只有 {command.Target?.Length ?? 0} 个坐标，按 XY 下发需要 2 个");
                }

                bool isArc = command is MotionCardArcCommand;
                double centerX = 0;
                double centerY = 0;

                if (command is MotionCardArcCommand arc)
                {
                    if (arc.Center is null || arc.Center.Length < 2)
                    {
                        throw new InvalidOperationException($"第 {arc.Mark} 段圆弧的 Center 不足 2 个坐标");
                    }

                    centerX = arc.Center[0];
                    centerY = arc.Center[1];
                }

                double mode = command.Mode == 1
                    ? ZmotionProgramContract.SegmentModeDispense
                    : ZmotionProgramContract.SegmentModeMove;

                double kind = isArc
                    ? ZmotionProgramContract.SegmentKindArc
                    : ZmotionProgramContract.SegmentKindLine;

                int @base = ZmotionProgramContract.TableBaseSegments + (i * ZmotionProgramContract.TableStride);

                AppendTableAssignment(text, @base + ZmotionProgramContract.TableOffsetEndX, command.Target[0]);
                AppendTableAssignment(text, @base + ZmotionProgramContract.TableOffsetEndY, command.Target[1]);
                AppendTableAssignment(text, @base + ZmotionProgramContract.TableOffsetSpeed, command.Speed);
                AppendTableAssignment(text, @base + ZmotionProgramContract.TableOffsetMode, mode);
                AppendTableAssignment(text, @base + ZmotionProgramContract.TableOffsetCenterX, centerX);
                AppendTableAssignment(text, @base + ZmotionProgramContract.TableOffsetCenterY, centerY);
                AppendTableAssignment(text, @base + ZmotionProgramContract.TableOffsetSegType, kind);
            }
        }

        /// <summary>IO 事件表：紧跟在段表之后。</summary>
        private static void AppendIoTable(StringBuilder text, MotionCardCommandSet commands)
        {
            text.AppendLine();
            text.AppendLine("' ---- IO 事件表（段号 / 段内行程 / 开关）----");

            if (commands.Io.Count == 0)
            {
                text.AppendLine("' （无 IO 事件）");
                return;
            }

            int ioBase = ZmotionProgramContract.TableBaseSegments +
                         (commands.Path.Count * ZmotionProgramContract.TableStride);

            for (int i = 0; i < commands.Io.Count; i++)
            {
                MotionCardIoCommand io = commands.Io[i];
                int @base = ioBase + (i * ZmotionProgramContract.IoStride);

                AppendTableAssignment(text, @base + ZmotionProgramContract.IoOffsetMark, io.Mark);
                AppendTableAssignment(text, @base + ZmotionProgramContract.IoOffsetDistance, io.DistanceOnSegment);
                AppendTableAssignment(text, @base + ZmotionProgramContract.IoOffsetOn, io.On ? 1 : 0);
            }
        }

        /// <summary>
        /// 常驻主循环：等位姿 → 自检 → 插补。
        /// </summary>
        /// <remarks>
        /// 两遍走是刻意的：第一遍只算变换后坐标并判软限位，第二遍才动。
        /// 这样"某颗产品折算出来会撞到行程尽头"是在动作之前被拒绝，而不是走到一半才停。
        /// </remarks>
        private static void AppendMainLoop(StringBuilder text)
        {
            text.AppendLine();
            text.AppendLine("' ============ 常驻循环：换型装载后，每颗产品只等位姿 ============");
            text.AppendLine("WHILE 1");
            text.AppendFormat(CultureInfo.InvariantCulture, "    VR({0}) = 0\n", ZmotionProgramContract.VrDone);
            text.AppendFormat(
                CultureInfo.InvariantCulture,
                "    WAIT UNTIL VR({0}) = 1        ' 等上位机写入位姿并请求\n",
                ZmotionProgramContract.VrRunRequest);
            text.AppendFormat(CultureInfo.InvariantCulture, "    VR({0}) = 0\n", ZmotionProgramContract.VrRunRequest);
            text.AppendLine();

            text.AppendLine("    IF ioCount > 0 THEN");
            text.AppendLine("        ' 骨架尚未生成阀门时序（PSO / HW_PSWITCH 参数序待核）：");
            text.AppendLine("        ' 宁可拒绝运行并报到上位机，也不空跑一遍轨迹而完全不出胶。");
            text.AppendFormat(
                CultureInfo.InvariantCulture,
                "        VR({0}) = {1}\n",
                ZmotionProgramContract.VrError,
                ZmotionProgramContract.ErrorIoNotImplemented);
            text.AppendFormat(CultureInfo.InvariantCulture, "        VR({0}) = 1\n", ZmotionProgramContract.VrDone);
            text.AppendLine("    ELSE");
            text.AppendFormat(CultureInfo.InvariantCulture, "        VR({0}) = 0\n", ZmotionProgramContract.VrError);
            text.AppendFormat(
                CultureInfo.InvariantCulture,
                "        dx = VR({0}) : dy = VR({1}) : th = VR({2}) : cx = VR({3}) : cy = VR({4})\n",
                ZmotionProgramContract.VrPoseDx,
                ZmotionProgramContract.VrPoseDy,
                ZmotionProgramContract.VrPoseTheta,
                ZmotionProgramContract.VrRotCenterX,
                ZmotionProgramContract.VrRotCenterY);
            text.AppendLine();

            // 第一遍：变换 + 软限位自检
            text.AppendLine("        ' ---- 第 1 遍：变换 + 软限位自检（把错误挡在动作之前）----");
            text.AppendLine("        FOR i = 0 TO segCount - 1");
            AppendTableRead(text, "            ", "x", ZmotionProgramContract.TableOffsetEndX);
            AppendTableRead(text, "            ", "y", ZmotionProgramContract.TableOffsetEndY);
            text.AppendLine("            tx = cx + (x - cx) * COS(th) - (y - cy) * SIN(th) + dx");
            text.AppendLine("            ty = cy + (x - cx) * SIN(th) + (y - cy) * COS(th) + dy");
            text.AppendFormat(
                CultureInfo.InvariantCulture,
                "            IF tx < {0} OR tx > {1} OR ty < {2} OR ty > {3} THEN\n",
                ZmotionProgramContract.SoftLimitXMin,
                ZmotionProgramContract.SoftLimitXMax,
                ZmotionProgramContract.SoftLimitYMin,
                ZmotionProgramContract.SoftLimitYMax);
            text.AppendFormat(
                CultureInfo.InvariantCulture,
                "                VR({0}) = 9002        ' 越软限位，拒绝启动\n",
                ZmotionProgramContract.VrError);
            text.AppendLine("            ENDIF");
            text.AppendLine("        NEXT i");
            text.AppendLine();

            // 第二遍：插补
            text.AppendLine("        ' ---- 第 2 遍：连续插补 ----");
            text.AppendFormat(
                CultureInfo.InvariantCulture,
                "        IF VR({0}) = 0 THEN\n",
                ZmotionProgramContract.VrError);
            text.AppendLine("            FOR i = 0 TO segCount - 1");
            AppendTableRead(text, "                ", "x", ZmotionProgramContract.TableOffsetEndX);
            AppendTableRead(text, "                ", "y", ZmotionProgramContract.TableOffsetEndY);
            AppendTableRead(text, "                ", "sp", ZmotionProgramContract.TableOffsetSpeed);
            AppendTableRead(text, "                ", "m", ZmotionProgramContract.TableOffsetMode);
            AppendTableRead(text, "                ", "ax", ZmotionProgramContract.TableOffsetCenterX);
            AppendTableRead(text, "                ", "ay", ZmotionProgramContract.TableOffsetCenterY);
            AppendTableRead(text, "                ", "st", ZmotionProgramContract.TableOffsetSegType);
            text.AppendLine("                tx = cx + (x - cx) * COS(th) - (y - cy) * SIN(th) + dx");
            text.AppendLine("                ty = cy + (x - cx) * SIN(th) + (y - cy) * COS(th) + dy");
            text.AppendLine("                ' 圆心同样要跟着转（刚体旋转，半径不变）——必须用旋转前的 ax/ay 计算");
            text.AppendLine("                acx = cx + (ax - cx) * COS(th) - (ay - cy) * SIN(th) + dx");
            text.AppendLine("                acy = cy + (ax - cx) * SIN(th) + (ay - cy) * COS(th) + dy");
            text.AppendLine("                SPEED = sp");
            text.AppendLine("                ' 待核：直线段 MOVEA(终点)；圆弧段 MOVECIRC(圆心, 终点)；缓冲/连续插补指令名待确认");
            text.AppendLine("                IF st = 1 THEN");
            text.AppendLine("                    MOVECIRC(acx, acy, tx, ty)");
            text.AppendLine("                ELSE");
            text.AppendLine("                    MOVEA(tx, ty)");
            text.AppendLine("                ENDIF");
            text.AppendLine("            NEXT i");
            text.AppendLine("            WAIT IDLE                  ' 待核：等待运动完成的指令名");
            text.AppendLine("        ENDIF");
            text.AppendLine();
            text.AppendFormat(CultureInfo.InvariantCulture, "        VR({0}) = 1\n", ZmotionProgramContract.VrDone);
            text.AppendLine("    ENDIF");
            text.AppendLine("WEND");
        }

        private static void AppendTableAssignment(StringBuilder text, int index, double value)
        {
            text.AppendFormat(
                CultureInfo.InvariantCulture,
                "TABLE({0}) = {1}{2}",
                index,
                Format(value),
                Environment.NewLine);
        }

        private static void AppendTableRead(StringBuilder text, string indent, string variable, int offset)
        {
            text.AppendFormat(
                CultureInfo.InvariantCulture,
                "{0}{1} = TABLE(i * {2} + {3}){4}",
                indent,
                variable,
                ZmotionProgramContract.TableStride,
                offset,
                Environment.NewLine);
        }

        private static string Format(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
    }
}
