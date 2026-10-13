using Core.Motion;                                    
using Core.Zmotion;                                  
using System;                                         
using System.Collections.Generic;                     
using System.Globalization;                           
using System.Linq;                                   
using System.Text;                                    

namespace Motion.Zmotion                                      
{
    /// <summary>
    /// 厂商无关运动指令集 → 正运动 ZBasic 常驻点胶程序转换器，支持 HW_PSWITCH2 Mode‑3 PSO 位置同步输出
    /// 输入：MotionCardCommandSet（Path 直线/圆弧 + Io 胶阀触发）
    /// 输出：MotionProgramPayload，可直接下载至 ZMC 控制器
    /// </summary>
    public sealed class ZmotionProgramWriter : IMotionProgramWriter   
    {
        private readonly string _programName;              // 只读字段：生成的 ZBasic 程序名（默认 DISPENSE）

        // 构造函数：校验程序名非空后保存
        public ZmotionProgramWriter(string programName = "DISPENSE")
        {
            if (string.IsNullOrWhiteSpace(programName))    // 程序名为空或仅空白字符则抛异常
                throw new ArgumentException("程序名不能为空", nameof(programName));
            _programName = programName;                    // 记录合法程序名
        }

        // 格式标识属性：声明本写入器产出的指令格式名称
        public string FormatName => "Zmotion‑ZBasic‑PSO‑Dispense";

        /// <summary>入口：接收标准厂商无关指令集 MotionCardCommandSet，生成可下发程序</summary>
        public MotionProgramPayload Write(MotionCardCommandSet commands)
        {
            if (commands == null) throw new ArgumentNullException(nameof(commands));   // 入参空校验
            if (commands.Path == null || commands.Path.Count == 0)                       // 轨迹 Path 为空校验
                throw new InvalidOperationException("轨迹Path为空，没有可下发的运动段");

            // ========== IO 预处理校验 ==========
            // 仅保留合法的胶阀触发 IO 指令，存入校验后的列表
            var ioValidList = new List<(int Mark, double DistanceOnSegment, bool On)>();
            foreach (var ioCmd in commands.Io)                                         // 遍历所有 IO 指令
            {
                if (ioCmd.Action is IoActionType.Delay)                                // 段内延时当前不支持
                {
                    throw new NotSupportedException($"本常驻ZBasic程序不支持IoActionType.Delay段内延时；Mark={ioCmd.Mark}");
                }
                if (ioCmd.Action is not (IoActionType.ValveOn or IoActionType.ValveOff)) // 只处理开/关胶阀，其余跳过
                    continue;

                // 校验 Mark 必须存在于 Path 段下标范围内
                if (ioCmd.Mark < 0 || ioCmd.Mark >= commands.Path.Count)
                {
                    throw new InvalidOperationException($"IO指令Mark={ioCmd.Mark}超出Path轨迹段范围");
                }
                ioValidList.Add((ioCmd.Mark, ioCmd.DistanceOnSegment, ioCmd.On));       // 加入合法列表
            }

            // 同一段 Mark 内按段内行程升序（PSO 硬件要求比较点从小到大，否则触发错位）
            var ioSorted = ioValidList
                .GroupBy(x => x.Mark)                                                  // 先按所属轨迹段分组
                .SelectMany(g => g.OrderBy(item => item.DistanceOnSegment))             // 段内再按行程距离升序后展平
                .ToList();

            var sb = new StringBuilder();                                              // 构建 ZBasic 程序文本
            AppendHeader(sb);                                                          // 写入文件头/契约说明注释
            AppendDeclarations(sb, commands.Path.Count, ioSorted.Count);               // 写入变量声明与常量
            AppendSegmentTable(sb, commands.Path);                                     // 写入轨迹 TABLE

            // IO 表真实基址：紧跟轨迹表之后（轨迹段数 * 步距 + 轨迹表基址）
            int ioRealBaseAddr = ZmotionProgramContract.TableBaseSegments
                                 + commands.Path.Count * ZmotionProgramContract.TableStride;
            AppendIoTable(sb, ioSorted, ioRealBaseAddr);                               // 写入 IO 事件 TABLE

            AppendMainLoop(sb);                                                        // 写入常驻主循环

            return new MotionProgramPayload(_programName, sb.ToString());              // 封装为可下发载荷返回
        }

        // 写入文件头与契约说明（纯注释，便于联调核对）
        private static void AppendHeader(StringBuilder sb)
        {
            sb.AppendLine("' ==================================================================");
            sb.AppendLine("' ZmotionProgramWriter auto‑generate ZBasic");
            sb.AppendLine("' 常驻点胶程序：下载一次，上位机VR下发工件位姿补偿 dx/dy/theta");
            sb.AppendLine("' 契约：Core.Zmotion.ZmotionProgramContract");
            sb.AppendLine("' PSO: HW_PSWITCH2 Mode3 基于XY合成矢量VECTOR_MOVED");
            sb.AppendLine("' ==================================================================");
            // 输出位姿补偿相关 VR 变量地址说明（仅注释，便于联调对照）
            sb.AppendFormat("' VR VrPoseDx={0},VrPoseDy={1},VrPoseTheta={2},VrRotCenterX={3},VrRotCenterY={4}{5}",
                ZmotionProgramContract.VrPoseDx,
                ZmotionProgramContract.VrPoseDy,
                ZmotionProgramContract.VrPoseTheta,
                ZmotionProgramContract.VrRotCenterX,
                ZmotionProgramContract.VrRotCenterY,
                Environment.NewLine);

            // 输出运行状态相关 VR 变量地址说明
            sb.AppendFormat("' VR RunReq={0},Done={1},Error={2},AbortReq={3},MoveOpDelay={4}{5}",
                ZmotionProgramContract.VrRunRequest,
                ZmotionProgramContract.VrDone,
                ZmotionProgramContract.VrError,
                ZmotionProgramContract.VrAbortRequest,
                ZmotionProgramContract.VrMoveOpDelay,
                Environment.NewLine);

            sb.AppendLine($"' TmpIoTableBase={ZmotionProgramContract.TmpIoTableBase}");        // 临时 IO 表基址注释
            sb.AppendLine($"' PsoPointTableBase={ZmotionProgramContract.PsoPointTableBase}");  // PSO 点表基址注释
            sb.AppendLine("' ==================================================================");
        }

        // 写入 ZBasic 局部变量声明、轴映射、常量定义
        private static void AppendDeclarations(StringBuilder sb, int segCount, int ioCount)
        {
            sb.AppendLine();
            sb.AppendLine("' -------- ZBasic局部变量 --------");
            sb.AppendLine("DIM i, segTotal, ioTotal, ioIdx, segIoCnt, k AS INTEGER");     // 循环/计数整型变量
            sb.AppendLine("DIM dx, dy, th, cx, cy AS FLOAT");                            // 位姿补偿：偏移/旋转角/旋转中心
            sb.AppendLine("DIM xEnd, yEnd, sp, mode, ax, ay, segKind AS FLOAT");         // 段终点/速度/模式/圆心/类型
            sb.AppendLine("DIM tx, ty, acx, acy AS FLOAT");                              // 变换后终点与圆心坐标
            sb.AppendLine("DIM triggerRelDist, outLevel, baseVectorPos AS FLOAT");       // PSO 触发距离/电平/基准矢量位置
            sb.AppendLine();

            sb.AppendLine("BASE(0,1)                    '【联调核对】轴映射 X=0 Y=1");    // 绑定 X、Y 轴到 BASE 0、1
            sb.AppendLine("UNITS = 100                  '【联调核对】脉冲当量");           // 单位脉冲数（每单位脉冲）
            sb.AppendLine();

            // 将段总数与 IO 事件总数写入变量，主循环中使用
            sb.AppendFormat(CultureInfo.InvariantCulture, "segTotal = {0}{1}", segCount, Environment.NewLine);
            sb.AppendFormat(CultureInfo.InvariantCulture, "ioTotal = {0}{1}", ioCount, Environment.NewLine);

            // 输出各类契约常量，确保上位机与控制器对表地址/端口/软限位理解一致
            sb.AppendLine($@"
                            CONST TableBaseSegments = {ZmotionProgramContract.TableBaseSegments}
                            CONST TableStride = {ZmotionProgramContract.TableStride}
                            CONST IoStride = {ZmotionProgramContract.IoStride}
                            CONST TmpIoTableBase = {ZmotionProgramContract.TmpIoTableBase}
                            CONST PsoPointTableBase = {ZmotionProgramContract.PsoPointTableBase}
                            CONST DispenseDoPort = {ZmotionProgramContract.DispenseDoPort}

                            CONST SoftLimitXMin = {ZmotionProgramContract.SoftLimitXMin.ToString(CultureInfo.InvariantCulture)}
                            CONST SoftLimitXMax = {ZmotionProgramContract.SoftLimitXMax.ToString(CultureInfo.InvariantCulture)}
                            CONST SoftLimitYMin = {ZmotionProgramContract.SoftLimitYMin.ToString(CultureInfo.InvariantCulture)}
                            CONST SoftLimitYMax = {ZmotionProgramContract.SoftLimitYMax.ToString(CultureInfo.InvariantCulture)}
                          ");
        }

        /// <summary>生成轨迹 TABLE：解析 IReadOnlyList&lt;MotionCardCommand&gt; 直线/圆弧</summary>
        private static void AppendSegmentTable(StringBuilder sb, IReadOnlyList<MotionCardCommand> path)
        {
            sb.AppendLine();
            sb.AppendLine("' --------轨迹表 TABLE（工件原始XY绝对坐标） --------");
            for (int idx = 0; idx < path.Count; idx++)                  // 遍历每一段运动指令
            {
                var cmd = path[idx];                                    // 当前段指令
                double endX = cmd.Target[0];                            // 段终点 X
                double endY = cmd.Target[1];                            // 段终点 Y
                double sp = cmd.Speed;                                  // 段运动速度
                double mode = cmd.Mode;                                 // 段模式（如绝对/相对插补模式）

                double centerX = 0;                                     // 圆弧圆心 X（直线时为 0）
                double centerY = 0;                                     // 圆弧圆心 Y（直线时为 0）
                double segKind = ZmotionProgramContract.SegmentKindLine; // 默认段类型：直线

                if (cmd is MotionCardArcCommand arcCmd)                 // 若为圆弧指令
                {
                    segKind = ZmotionProgramContract.SegmentKindArc;    // 段类型标记为圆弧
                    centerX = arcCmd.Center[0];                         // 圆弧圆心 X
                    centerY = arcCmd.Center[1];                         // 圆弧圆心 Y
                }

                // 计算本段在 TABLE 中的起始索引：基址 + 段序号 * 步距
                int baseIdx = ZmotionProgramContract.TableBaseSegments + idx * ZmotionProgramContract.TableStride;
                AssignTable(sb, baseIdx + ZmotionProgramContract.TableOffsetEndX, endX);     // 写终点 X
                AssignTable(sb, baseIdx + ZmotionProgramContract.TableOffsetEndY, endY);     // 写终点 Y
                AssignTable(sb, baseIdx + ZmotionProgramContract.TableOffsetSpeed, sp);      // 写速度
                AssignTable(sb, baseIdx + ZmotionProgramContract.TableOffsetMode, mode);      // 写模式
                AssignTable(sb, baseIdx + ZmotionProgramContract.TableOffsetCenterX, centerX); // 写圆心 X
                AssignTable(sb, baseIdx + ZmotionProgramContract.TableOffsetCenterY, centerY); // 写圆心 Y
                AssignTable(sb, baseIdx + ZmotionProgramContract.TableOffsetSegType, segKind); // 写段类型（直线/圆弧）
            }
        }

        /// <summary>【已修复】IO表按列表顺序连续写入，使用循环j作为下标，不用item.MarkIndex</summary>
        private static void AppendIoTable(StringBuilder sb, List<(int Mark, double DistanceOnSegment, bool On)> ioList, int ioBaseAddr)
        {
            sb.AppendLine();
            sb.AppendLine("' --------IO事件表 TABLE，紧跟轨迹表之后，按预处理后顺序连续存储 --------");
            if (ioList.Count == 0)                                      // 无胶阀触发事件
            {
                sb.AppendLine("' 无胶阀IO触发事件");
                return;
            }
            for (int j = 0; j < ioList.Count; j++)                      // 按预处理后顺序（段内升序）连续写入
            {
                var item = ioList[j];                                   // 当前 IO 事件
                int b = ioBaseAddr + j * ZmotionProgramContract.IoStride; // 连续地址 = IO 基址 + 顺序下标 * 步距
                AssignTable(sb, b + ZmotionProgramContract.IoOffsetMark, item.Mark);                 // 写所属段 Mark
                AssignTable(sb, b + ZmotionProgramContract.IoOffsetDistance, item.DistanceOnSegment); // 写段内触发距离
                AssignTable(sb, b + ZmotionProgramContract.IoOffsetOn, item.On ? 1.0 : 0.0);         // 写开/关（1/0）
            }
        }

        // 写入常驻主循环：下载一次，等位姿 VR 触发加工
        private static void AppendMainLoop(StringBuilder sb)
        {
            sb.AppendLine();
            sb.AppendLine("' ============常驻主循环：下载一次，等位姿VR触发加工 ============");
            sb.AppendLine("WHILE 1");                                     // 死循环，常驻运行
            sb.AppendFormat(CultureInfo.InvariantCulture, "    VR({0}) = 0\n", ZmotionProgramContract.VrDone);   // 清除完成标志
            sb.AppendFormat(CultureInfo.InvariantCulture, "    WAIT UNTIL VR({0}) = 1\n", ZmotionProgramContract.VrRunRequest); // 等待上位机下发运行请求
            sb.AppendFormat(CultureInfo.InvariantCulture, "    VR({0}) = 0\n", ZmotionProgramContract.VrRunRequest); // 清除运行请求（已捕获）
            sb.AppendFormat(CultureInfo.InvariantCulture, "    VR({0}) = 0\n", ZmotionProgramContract.VrError);   // 清除错误标志

            // 读取上位机下发的工件位姿补偿：X/Y 偏移、旋转角、旋转中心
            sb.AppendFormat(CultureInfo.InvariantCulture,
                "    dx=VR({0}): dy=VR({1}): th=VR({2}): cx=VR({3}): cy=VR({4}){5}",
                ZmotionProgramContract.VrPoseDx,
                ZmotionProgramContract.VrPoseDy,
                ZmotionProgramContract.VrPoseTheta,
                ZmotionProgramContract.VrRotCenterX,
                ZmotionProgramContract.VrRotCenterY,
                Environment.NewLine);

            // 第一遍：刚体坐标变换 + 软限位预校验，只计算不运动，提前拦截撞机
            sb.AppendLine("    '=====第一遍：刚体坐标变换 + 软限位预校验，只计算不运动，提前拦截撞机=====");
            sb.AppendLine("    FOR i = 0 TO segTotal -1");                // 遍历所有段
            // ReadTable已修复：读取带上TableBaseSegments基址，TableBaseSegments!=0不会读写错位
            ReadTable(sb, "        ", "xEnd", ZmotionProgramContract.TableOffsetEndX);   // 读原始终点 X
            ReadTable(sb, "        ", "yEnd", ZmotionProgramContract.TableOffsetEndY);   // 读原始终点 Y
            // 刚体变换（绕旋转中心旋转 th 再平移 dx/dy）得到实际终点
            sb.AppendLine("        tx = cx + (xEnd - cx)*COS(th) - (yEnd - cy)*SIN(th) + dx");
            sb.AppendLine("        ty = cy + (xEnd - cx)*SIN(th) + (yEnd - cy)*COS(th) + dy");
            // 软限位检查：超出工作范围则置错误码
            sb.AppendLine(@"
        IF tx < SoftLimitXMin OR tx > SoftLimitXMax OR ty < SoftLimitYMin OR ty > SoftLimitYMax THEN
            VR(" + ZmotionProgramContract.VrError + ") = " + ZmotionProgramContract.ErrorSoftLimitExceeded + @"
        ENDIF
");
            sb.AppendLine("    NEXT i");                                 // 第一遍结束

            // 若第一遍发现软限位超限，直接置完成并返回等待下一轮
            sb.AppendLine($"    IF VR({ZmotionProgramContract.VrError})<>0 THEN");
            sb.AppendFormat(CultureInfo.InvariantCulture, "        VR({0})=1\n", ZmotionProgramContract.VrDone);
            sb.AppendLine("        CONTINUE WHILE");                     // 跳过本轮加工
            sb.AppendLine("    ENDIF");

            // 第二遍：插补运动 + HW_PSWITCH2 Mode3 PSO 点胶输出
            sb.AppendLine("    '=====第二遍：插补运动 + HW_PSWITCH2 Mode3 PSO点胶输出=====");
            sb.AppendLine("    FOR i = 0 TO segTotal -1");               // 遍历所有段进行实际运动
            ReadTable(sb, "        ", "xEnd", ZmotionProgramContract.TableOffsetEndX);    // 读原始终点 X
            ReadTable(sb, "        ", "yEnd", ZmotionProgramContract.TableOffsetEndY);    // 读原始终点 Y
            ReadTable(sb, "        ", "sp", ZmotionProgramContract.TableOffsetSpeed);      // 读速度
            ReadTable(sb, "        ", "mode", ZmotionProgramContract.TableOffsetMode);      // 读模式
            ReadTable(sb, "        ", "ax", ZmotionProgramContract.TableOffsetCenterX);     // 读圆弧圆心 X
            ReadTable(sb, "        ", "ay", ZmotionProgramContract.TableOffsetCenterY);     // 读圆弧圆心 Y
            ReadTable(sb, "        ", "segKind", ZmotionProgramContract.TableOffsetSegType); // 读段类型

            sb.AppendLine("        '刚体变换：终点、圆弧圆心");
            sb.AppendLine("        tx = cx + (xEnd - cx)*COS(th) - (yEnd - cy)*SIN(th) + dx");  // 变换终点 X
            sb.AppendLine("        ty = cy + (xEnd - cx)*SIN(th) + (yEnd - cy)*COS(th) + dy");  // 变换终点 Y
            sb.AppendLine("        acx = cx + (ax - cx)*COS(th) - (ay - cy)*SIN(th) + dx");      // 变换圆心 X
            sb.AppendLine("        acy = cy + (ax - cx)*SIN(th) + (ay - cy)*COS(th) + dy");      // 变换圆心 Y
            sb.AppendLine("        SPEED = sp");                          // 设置本段速度

            // 运行过程中检查中止请求，若置位则中断循环
            sb.AppendFormat(CultureInfo.InvariantCulture, "        IF VR({0})<>0 THEN BREAK FOR ENDIF{1}",
                ZmotionProgramContract.VrAbortRequest, Environment.NewLine);

            // 本段运动执行体（关闭旧 PSO、筛选本段 IO、配置 Mode3、执行插补、等待完成）
            sb.AppendLine(@"
                            HW_PSWITCH2(2) '关闭上一段残留PSO比较器，防止误触发
                            segIoCnt = 0
                            '筛选IO表属于当前段i的全部胶阀触发事件；IO基址运行时公式计算
                            FOR ioIdx = 0 TO ioTotal -1
                                ioBase = TableBaseSegments + segTotal * TableStride + ioIdx * IoStride
                                ioSeg = TABLE(ioBase + " + ZmotionProgramContract.IoOffsetMark + @")
                                IF ioSeg = i THEN
                                    TABLE(TmpIoTableBase + segIoCnt*2 +0) = TABLE(ioBase + " + ZmotionProgramContract.IoOffsetDistance + @")
                                    TABLE(TmpIoTableBase + segIoCnt*2 +1) = TABLE(ioBase + " + ZmotionProgramContract.IoOffsetOn + @")
                                    segIoCnt = segIoCnt +1
                                ENDIF
                            NEXT ioIdx

                            IF segIoCnt > 0 THEN
                                baseVectorPos = VECTOR_MOVED(0,1)
                                '段内相对距离换算为全局XY合成矢量比较位置
                                FOR k = 0 TO segIoCnt -1
                                    triggerRelDist = TABLE(TmpIoTableBase + k*2 +0)
                                    TABLE(PsoPointTableBase + k) = baseVectorPos + triggerRelDist
                                NEXT k
                                '启动Mode3：XY合成行走距离PSO输出；MOVEOP_DELAY由VR动态下发，实现胶阀提前触发
                                HW_PSWITCH2(3, DispenseDoPort, TABLE(TmpIoTableBase+1), PsoPointTableBase, PsoPointTableBase + segIoCnt -1,1)
                                MOVEOP_DELAY = VR(" + ZmotionProgramContract.VrMoveOpDelay + @")
                            ENDIF

                            '执行插补
                            IF segKind = " + ZmotionProgramContract.SegmentKindArc + @" THEN
                                MOVECIRC(acx,acy,tx,ty)
                            ELSE
                                MOVEA(tx,ty)
                            ENDIF

                            WAIT IDLE(0,1)     '等待本段XY轴运动执行完毕
                            HW_PSWITCH2(2)     '关闭PSO，切断胶阀输出
            ");
            sb.AppendLine("    NEXT i");                             // 第二遍段循环结束

                                // 运行收尾：处理中止、置完成标志
             sb.AppendFormat(CultureInfo.InvariantCulture, @"
                        WAIT IDLE(0,1)
                        IF VR({0}) =1 THEN
                            VR({1}) = {2}
                            VR({0})=0
                        ENDIF
                        VR({3})=1
                    WEND
                    ",
                ZmotionProgramContract.VrAbortRequest,
                ZmotionProgramContract.VrError,
                ZmotionProgramContract.ErrorAborted,
                ZmotionProgramContract.VrDone);
        }

        // 向 TABLE 指定索引写入一个双精度值（固定四位小数格式）
        private static void AssignTable(StringBuilder sb, int idx, double val)
        {
            sb.AppendFormat(CultureInfo.InvariantCulture, "TABLE({0})={1}{2}", idx, val.ToString("0.####", CultureInfo.InvariantCulture), Environment.NewLine);
        }

        /// <summary>【修复】读取轨迹TABLE带上TableBaseSegments基址，避免TableBaseSegments非0时读写错位</summary>
        private static void ReadTable(StringBuilder sb, string indent, string varName, int offset)
        {
            // 生成的 ZBasic：varName = TABLE(TableBaseSegments + i*TableStride + offset)
            // 与 AppendSegmentTable 的写入地址（TableBaseSegments + idx*TableStride + offset）保持一致
            sb.AppendFormat(CultureInfo.InvariantCulture, "{0}{1}=TABLE(TableBaseSegments + i*{2}+{3}){4}",
                indent, varName, ZmotionProgramContract.TableStride, offset, Environment.NewLine);
        }
    }
}
