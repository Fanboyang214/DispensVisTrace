using Core.Motion.Zmotion;
using System;
using System.IO;
using System.Text;
using ZMotionSDK;
using ZMotionSDK.Models;

namespace Motion.Zmotion
{
    /// <summary>
    /// <see cref="IZmotionApi"/> 的<b>真库实现</b>：映射到 <c>ZMotionSDK</c>（NuGet，2.1.1）。
    /// </summary>
    /// <remarks>
    /// <para><b>SDK 有两套 API，这里两边都用</b>（这是实测确认的，不是猜的）：
    /// <list type="bullet">
    /// <item><c>ZMotionSDK.ZMotion</c>（OO 门面，147 成员）：<c>Open(ip, timeout)</c> / <c>Close()</c> /
    /// <c>Handle</c> / <c>BasDown(path, mode)</c> / <c>Execute_Buffer(command)</c> /
    /// <c>Stop_All(CancelMode)</c> / 轴状态与参数。<b>门面里没有 VR 方法</b>。</item>
    /// <item><c>ZMotionSDK.zmcaux</c>（handle 式，329 个 <b>static</b> 成员）：
    /// VR 读写 <c>ZAux_Direct_SetVrf/GetVrf(handle, start, num, float[])</c>、
    /// TABLE <c>ZAux_Direct_SetTable/GetTable</c>、
    /// 位置比较输出 <c>ZAux_Direct_Pswitch(handle, num, enable, axisnum, outnum, outstate, setpos, resetpos)</c>
    /// 与硬件版 <c>ZAux_Direct_HwPswitch(handle, axisnum, mode, direction, reserve, tablestart, tableend)</c>、
    /// 轴组 <c>ZAux_Direct_Base</c>、急停 <c>ZAux_Direct_Rapidstop</c>、通信超时 <c>ZAux_SetTimeOut</c>。</item>
    /// </list>
    /// </para>
    /// <para><b>待核清单（联调时逐条落定，实现处也各自标了"待核"）</b>：
    /// <list type="number">
    /// <item><b>程序下载</b>：<c>BasDown</c> 只接受<b>文件路径</b>，所以这里落一个临时文件。
    /// 内存下发的正路是 <c>ZAux_3FileRamDownBegin/Part/End</c> +
    /// <c>ZAux_Run3FileRam(handle, ifile3num, itasknum)</c>，还能用
    /// <c>ZAux_3FileRamGetRemainSpace</c> 的差值证明"确实写进去了"——建议联调改走这条。</item>
    /// <item><b>程序是否存在</b>：SDK 没有文件存在性查询，所以
    /// <see cref="SupportsProgramReadBack"/> 先返回 <c>false</c>（服务会据此跳过回读校验并记录），
    /// 等 3FileRam 空间差值或 BASIC 文件指令确认后再打开。</item>
    /// <item><b>运行常驻程序</b>：用 <c>Execute_Buffer("RUN \"&lt;名字&gt;.bas\"")</c>（BASIC 的 RUN 命令，
    /// 带不带扩展名、要不要指定任务号待核）。<see cref="Run"/> 幂等：一次下载只起一次任务，
    /// 之后每颗产品只靠 VR 触发。</item>
    /// <item><b>下载模式</b>：默认 <c>BasDownMode.ROM</c>（掉电保存，换型才写一次）。
    /// 若担心 Flash 写入寿命或要更快的换型，可改 <c>RAM</c>——代价是控制器重启后需重新下载。</item>
    /// </list>
    /// </para>
    /// <para>
    /// <b>线程安全</b>：句柄不保证可重入，本类内部一把锁把所有调用串行化。
    /// 「逐调用锁 + 命令序列锁」的分级（避免长装载堵住读点）留到联调测出装载耗时后再按需拆。
    /// </para>
    /// </remarks>
    public sealed class ZauxZmotionApi : IZmotionApi
    {
        private readonly object gate = new object();
        private readonly ZMotion zmotion = new ZMotion();

        /// <summary>单值 VR 读写的复用缓冲（锁内使用，不跨线程）。</summary>
        private readonly float[] singleValue = new float[1];

        private bool connected;
        private string? programName;
        private bool programStarted;

        /// <summary>连接超时（ms）。</summary>
        public uint ConnectTimeoutMs { get; init; } = 3000;

        /// <summary>通信超时（ms），连接成功后下发 <c>ZAux_SetTimeOut</c>。</summary>
        public uint IoTimeoutMs { get; init; } = 3000;

        /// <summary>下载模式；默认 ROM（掉电保存）。</summary>
        public BasDownMode DownloadMode { get; init; } = BasDownMode.ROM;

        /// <inheritdoc />
        /// <remarks>
        /// 先返回 <c>false</c>：SDK 未暴露文件存在性查询，服务据此跳过回读校验并在诊断里标明。
        /// 换成 3FileRam 空间差值校验后改成 <c>true</c>。
        /// </remarks>
        public bool SupportsProgramReadBack => false;

        /// <inheritdoc />
        public bool IsConnected
        {
            get
            {
                lock (gate)
                {
                    return connected;
                }
            }
        }

        /// <inheritdoc />
        public void Open(string endpoint)
        {
            if (string.IsNullOrWhiteSpace(endpoint))
            {
                throw new ArgumentException("endpoint 不能为空", nameof(endpoint));
            }

            lock (gate)
            {
                if (connected)
                {
                    return;   // 幂等（宿主也保证了，这里再兜一层）
                }

                zmotion.Open(endpoint, ConnectTimeoutMs);
                Check(zmcaux.ZAux_SetTimeOut(zmotion.Handle, IoTimeoutMs), "设置通信超时");
                connected = true;
            }
        }

        /// <inheritdoc />
        public void Close()
        {
            lock (gate)
            {
                if (!connected)
                {
                    return;
                }

                zmotion.Close();
                connected = false;
                programStarted = false;
            }
        }

        /// <inheritdoc />
        public void DownloadProgram(string name, string text)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentException("程序名不能为空", nameof(name));
            }

            lock (gate)
            {
                EnsureConnected();

                // 待核：BasDown 只吃文件路径 → 落临时文件；正路是 ZAux_3FileRamDown*（内存下发）
                string path = Path.Combine(Path.GetTempPath(), name + ".bas");
                File.WriteAllText(path, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                zmotion.BasDown(path, DownloadMode);

                programName = name;
                programStarted = false;
            }
        }

        /// <inheritdoc />
        public bool ProgramExists(string name)
        {
            lock (gate)
            {
                // 待核：这不是"从卡上回读"，只是"本次会话下载过这个名字"。
                // 真正的回读要等 3FileRam 空间差值 / BASIC 文件指令确认（见 SupportsProgramReadBack）。
                return programName is not null && string.Equals(programName, name, StringComparison.Ordinal);
            }
        }

        /// <inheritdoc />
        public void SetCurrentProgram(string name)
        {
            lock (gate)
            {
                programName = name;
                programStarted = false;
            }
        }

        /// <inheritdoc />
        public void Run()
        {
            lock (gate)
            {
                EnsureConnected();

                if (programStarted || programName is null)
                {
                    return;   // 幂等：常驻程序只起一次，之后靠 VR 触发
                }

                // 待核：BASIC 的 RUN 命令格式（扩展名 / 任务号）
                zmotion.Execute_Buffer($"RUN \"{programName}.bas\"");
                programStarted = true;
            }
        }

        /// <inheritdoc />
        public void Stop()
        {
            lock (gate)
            {
                EnsureConnected();

                // 取消当前运动 + 缓冲运动（枚举成员就是中文，SDK 如此）
                zmotion.Stop_All(CancelMode.取消当前运动和缓冲运动);
            }
        }

        /// <inheritdoc />
        public void WriteVr(int index, float value)
        {
            lock (gate)
            {
                EnsureConnected();
                singleValue[0] = value;
                Check(zmcaux.ZAux_Direct_SetVrf(zmotion.Handle, index, 1, singleValue), $"写 VR({index})");
            }
        }

        /// <inheritdoc />
        public float ReadVr(int index)
        {
            lock (gate)
            {
                EnsureConnected();
                singleValue[0] = 0f;
                Check(zmcaux.ZAux_Direct_GetVrf(zmotion.Handle, index, 1, singleValue), $"读 VR({index})");
                return singleValue[0];
            }
        }

        /// <summary>把 SDK 的错误码转成带上下文的异常（错误码说明由 SDK 的 <c>CheckResult</c> 给出）。</summary>
        private void Check(int result, string what)
        {
            if (result == 0)
            {
                return;
            }

            try
            {
                zmotion.CheckResult(result);
                throw new InvalidOperationException($"{what} 失败，错误码 {result}");
            }
            catch (ZMotionException ex)
            {
                throw new InvalidOperationException($"{what} 失败（错误码 {result}）：{ex.Message}", ex);
            }
        }

        private void EnsureConnected()
        {
            if (!connected)
            {
                throw new InvalidOperationException("正运动卡未连接（Open 由 ZmotionCardHost 调用）");
            }
        }

        /// <summary>
        /// 获取轴位置 mm
        /// </summary>
        /// <param name="axis"></param>
        /// <returns></returns>
        /// <exception cref="NotImplementedException"></exception>
        public float GetDpos(int axis)
        {
            
            lock (gate)
            {
                EnsureConnected();
                float pfValue = 0;
                Check(zmcaux.ZAux_Direct_GetDpos(zmotion.Handle, axis, ref pfValue), $"读axis{axis}位置");
                return pfValue;
            }
        }
        /// <summary>
        /// 轴速度
        /// </summary>
        /// <param name="axis"></param>
        /// <returns></returns>
        public float GetSpeed(int axis)
        {
            lock (gate)
            {
                EnsureConnected();
                float pfValue = 0;
                Check(zmcaux.ZAux_Direct_GetSpeed(zmotion.Handle, axis, ref pfValue), $"读axis{axis}速度");
                return pfValue;
            }
        }
        /// <summary>
        /// 轴运动完成状态
        /// </summary>
        /// <param name="axis"></param>
        /// <returns></returns>
        public bool GetIsIdle(int axis)
        {
            lock (gate)
            {
                EnsureConnected();
                int pfValue = 0;
                Check(zmcaux.ZAux_Direct_GetIfIdle(zmotion.Handle, axis, ref pfValue), $"读axis{axis}运动完成状态");
                return pfValue != 0;
            }
        }
        /// <summary>
        /// 轴回零完成状态
        /// </summary>
        /// <param name="axis"></param>
        /// <returns></returns>
        public bool GetHomeStatus(int axis)
        {
            lock (gate)
            {
                EnsureConnected();
                uint pfValue = 0;
                Check(zmcaux.ZAux_Direct_GetHomeStatus(zmotion.Handle, axis, ref pfValue), $"读axis{axis}回零完成状态");
                return pfValue != 0;
            }
        }
        /// <summary>
        /// 单轴绝对运动
        /// </summary>
        /// <param name="axis"></param>
        /// <param name="position"></param>
        /// <exception cref="NotImplementedException"></exception>
        public void MoveAbsolute(int axis, float position)
        {
            lock (gate)
            {
                EnsureConnected();
                
                Check(zmcaux.ZAux_Direct_Single_MoveAbs(zmotion.Handle, axis, position), $"axis{axis}绝对运动{position}mm");
                return ;
            }
        }
        

        /// <summary>
        /// 轴jog
        /// </summary>
        /// <param name="axis"></param>
        /// <param name="forward"></param>
        public void Jog(int axis, bool forward)
        {
            lock (gate)
            {
                EnsureConnected();
                int isForward = forward ? 1 : -1;
                Check(zmcaux.ZAux_Direct_Single_Vmove(zmotion.Handle, axis, isForward), $"axis{axis}jog运动{(forward ? "正" : "反")}向");
                return ;
            }
        }
        /// <summary>
        /// 停止轴jog
        /// </summary>
        /// <param name="axis"></param>
        public void StopJog(int axis)
        {
            lock (gate)
            {
                EnsureConnected();
                Check(zmcaux.ZAux_Direct_Single_Cancel(zmotion.Handle, axis,0), $"axis{axis}停止jog运动");
                return;
            }
        }

        /// <inheritdoc />
        public void MoveRelative(int axis, float distance)
        {
            lock (gate)
            {
                EnsureConnected();
                zmotion.Move_Relative(axis, distance);
            }
        }

        /// <inheritdoc />
        public void GoHome(int axis, int mode)
        {
            lock (gate)
            {
                EnsureConnected();
                zmotion.GoHome(axis, mode);
            }
        }

        /// <inheritdoc />
        public void SetHomeOffset(int axis, float offset)
        {
            lock (gate)
            {
                EnsureConnected();
                zmotion.SetGoHomeOffpos((uint)axis, offset);
            }
        }

        /// <inheritdoc />
        public float GetHomeOffset(int axis)
        {
            lock (gate)
            {
                EnsureConnected();
                return zmotion.GetGoHomeOffpos((uint)axis);
            }
        }

        /// <inheritdoc />
        public IReadOnlyList<AxisMotionSnapshot> ReadAxisStates(int axisCount)
        {
            if (axisCount <= 0)
            {
                return Array.Empty<AxisMotionSnapshot>();
            }

            lock (gate)
            {
                EnsureConnected();

                AxisMotionState[] states = zmotion.GetAxisMotionState(axisCount)
                    ?? Array.Empty<AxisMotionState>();

                int count = Math.Min(axisCount, states.Length);
                var result = new AxisMotionSnapshot[count];

                for (int axis = 0; axis < count; axis++)
                {
                    AxisMotionState state = states[axis];
                    AxisSignal signal = zmotion.GetAxisSignal(axis);

                    result[axis] = new AxisMotionSnapshot(
                        axis,
                        state.CurrentPosition,
                        state.Encoder,
                        state.CurrentSpeed,
                        state.IsRunning,
                        zmotion.GetHomeStatus(axis),
                        signal.PositiveLimitSignal,
                        signal.NegativeLimitSignal,
                        signal.AlarmSignal);
                }

                return result;
            }
        }

        /// <inheritdoc />
        public float ReadAxisParameter(int axis, AxisParameter parameter)
        {
            lock (gate)
            {
                EnsureConnected();
                return zmotion.GetParam(axis, ToReadName(parameter));
            }
        }

        /// <inheritdoc />
        public void WriteAxisParameter(int axis, AxisParameter parameter, float value)
        {
            lock (gate)
            {
                EnsureConnected();
                zmotion.SetParam(axis, ToWriteName(parameter), value);
            }
        }

        /// <summary>业务枚举 → SDK 的写参数名（名字就是控制器 BASIC 参数名）。</summary>
        private static WriteBaiscParmName ToWriteName(AxisParameter parameter) => parameter switch
        {
            AxisParameter.Units => WriteBaiscParmName.Units,
            AxisParameter.StartSpeed => WriteBaiscParmName.Lspeed,
            AxisParameter.Speed => WriteBaiscParmName.Speed,
            AxisParameter.Accel => WriteBaiscParmName.Accel,
            AxisParameter.Decel => WriteBaiscParmName.Decel,
            AxisParameter.Sramp => WriteBaiscParmName.Sramp,
            AxisParameter.FastDec => WriteBaiscParmName.Fastdec,
            AxisParameter.PositiveLimit => WriteBaiscParmName.Fs_Limit,
            AxisParameter.NegativeLimit => WriteBaiscParmName.Rs_Limit,
            AxisParameter.Creep => WriteBaiscParmName.Creep,
            AxisParameter.HomeFastSpeed => WriteBaiscParmName.Fhspeed,
            AxisParameter.DatumInput => WriteBaiscParmName.Datum_In,
            AxisParameter.ForwardLimitInput => WriteBaiscParmName.Fwd_In,
            AxisParameter.ReverseLimitInput => WriteBaiscParmName.Rev_In,
            AxisParameter.CornerMode => WriteBaiscParmName.Corner_Mode,
            AxisParameter.DecelAngle => WriteBaiscParmName.Decel_Angle,
            AxisParameter.StopAngle => WriteBaiscParmName.Stop_Angle,
            AxisParameter.FullSpeedRadius => WriteBaiscParmName.Full_Sp_Radius,
            AxisParameter.SpeedLimitRadius => WriteBaiscParmName.Splimit_Radius,
            _ => throw new ArgumentOutOfRangeException(nameof(parameter), parameter, "未映射到控制器写参数")
        };

        /// <summary>业务枚举 → SDK 的读参数名。</summary>
        private static ReadBaiscParmName ToReadName(AxisParameter parameter) => parameter switch
        {
            AxisParameter.Units => ReadBaiscParmName.Units,
            AxisParameter.StartSpeed => ReadBaiscParmName.Lspeed,
            AxisParameter.Speed => ReadBaiscParmName.Speed,
            AxisParameter.Accel => ReadBaiscParmName.Accel,
            AxisParameter.Decel => ReadBaiscParmName.Decel,
            AxisParameter.Sramp => ReadBaiscParmName.Sramp,
            AxisParameter.FastDec => ReadBaiscParmName.Fastdec,
            AxisParameter.PositiveLimit => ReadBaiscParmName.Fs_Limit,
            AxisParameter.NegativeLimit => ReadBaiscParmName.Rs_Limit,
            AxisParameter.Creep => ReadBaiscParmName.Creep,
            AxisParameter.HomeFastSpeed => ReadBaiscParmName.Fhspeed,
            AxisParameter.DatumInput => ReadBaiscParmName.Datum_In,
            AxisParameter.ForwardLimitInput => ReadBaiscParmName.Fwd_In,
            AxisParameter.ReverseLimitInput => ReadBaiscParmName.Rev_In,
            AxisParameter.CornerMode => ReadBaiscParmName.Corner_Mode,
            AxisParameter.DecelAngle => ReadBaiscParmName.Decel_Angle,
            AxisParameter.StopAngle => ReadBaiscParmName.Stop_Angle,
            AxisParameter.FullSpeedRadius => ReadBaiscParmName.Full_Sp_Radius,
            AxisParameter.SpeedLimitRadius => ReadBaiscParmName.Splimit_Radius,
            _ => throw new ArgumentOutOfRangeException(nameof(parameter), parameter, "未映射到控制器读参数")
        };
    }
}
