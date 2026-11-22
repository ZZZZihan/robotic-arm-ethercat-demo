using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using ScaraArm.Console.Theme;
using ScaraArm.Core.Kinematics;
using ScaraArm.Core.Motion;
using ScaraArm.Core.Protocol;
using ScaraArm.Core.Transport;

namespace ScaraArm.Console
{
    /// <summary>
    /// Hardware session lifecycle, polling, jog keepalive, E-Stop, and motion command execution for MainForm.
    /// </summary>
    internal sealed partial class MainForm
    {
        private void OnMainFormClosing(object sender, FormClosingEventArgs e)
        {
            formClosing = true;
            pollTimer.Stop();
            jogTimer.Stop();
            bool shouldStop = jogActive || (liveStatus != null && liveStatus.MotionState != 0);
            jogActive = false;
            jogGeneration++;
            CancelAllInFlightOps();
            if (sc4iClient.IsConnected && shouldStop)
            {
                try
                {
                    using (CancellationTokenSource cts = new CancellationTokenSource(500))
                    {
                        sc4iClient.StopHoldAsync(cts.Token).GetAwaiter().GetResult();
                    }
                }
                catch { }
            }
            sc4iClient.Disconnect();
        }

        private async Task ToggleConnectAsync()
        {
            if (busy) return;
            if (sc4iClient.IsConnected)
            {
                await DisconnectAsync();
                return;
            }

            busy = true;
            sessionLost = false;
            heartbeatFailCount = 0;
            SetStatus("正在连接控制卡…", UiTheme.Accent);
            RefreshUi();
            try
            {
                liveStatus = await RunCancellablePhaseAsync(
                    6000, ct => sc4iClient.ConnectAsync(endpoint.LocalIp, endpoint.LocalPort, endpoint.CardIp, endpoint.CardPort, 2000, ct));
                if (IsDisposed || Disposing) return;

                if (!liveStatus.BusReady)
                {
                    SetStatus("已连接，正在初始化总线（电机不上使能）…", UiTheme.Accent);
                    liveStatus = await RunCancellablePhaseAsync(30000, ct => sc4iClient.InitBusToOpAsync(ct));
                    if (IsDisposed || Disposing) return;
                }

                heartbeatFailCount = 0;
                sessionLost = false;
                pollTimer.Start();
                string readyMsg = liveStatus.EnabledMask == 0
                    ? "已连接，总线就绪（电机未使能）。"
                    : "已连接，总线就绪（已有轴处于使能/持力状态）。";
                SetStatus(readyMsg, UiTheme.Success);
            }
            catch (Exception error)
            {
                sc4iClient.Disconnect();
                liveStatus = null;
                sessionLost = false;
                if (!formClosing && !IsDisposed && !Disposing)
                {
                    SetStatus("连接失败：" + error.Message, UiTheme.Danger);
                    MessageBox.Show(this, error.Message, "连接失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
            finally
            {
                busy = false;
                if (!formClosing && !IsDisposed && !Disposing) RefreshUi();
            }
        }

        private async Task DisconnectAsync()
        {
            busy = true;
            pollTimer.Stop();
            jogTimer.Stop();
            bool shouldStop = jogActive || (liveStatus != null && liveStatus.MotionState != 0);
            jogActive = false;
            jogGeneration++;
            CancelAllInFlightOps();
            RefreshUi();
            try
            {
                if (shouldStop)
                {
                    try
                    {
                        using (CancellationTokenSource cts = new CancellationTokenSource(1000))
                        {
                            await sc4iClient.StopHoldAsync(cts.Token);
                        }
                    }
                    catch { }
                }
                sc4iClient.Disconnect();
                liveStatus = null;
                heartbeatFailCount = 0;
                sessionLost = false;
                if (!IsDisposed && !Disposing) SetStatus("已断开连接。", UiTheme.Muted);
            }
            finally
            {
                busy = false;
                if (!IsDisposed && !Disposing) RefreshUi();
            }
        }

        private async Task<Sc4iStatusReply> RunCancellablePhaseAsync(
            int timeoutMs, Func<CancellationToken, Task<Sc4iStatusReply>> work)
        {
            CancellationTokenSource cts = new CancellationTokenSource(timeoutMs);
            CancelAndDisposeCts(ref activeOpCts);
            activeOpCts = cts;
            try
            {
                return await work(cts.Token);
            }
            finally
            {
                if (ReferenceEquals(activeOpCts, cts)) activeOpCts = null;
                cts.Dispose();
            }
        }

        private async Task RunExclusiveCommandAsync(
            string startMessage,
            int timeoutMs,
            Func<CancellationToken, Task<Sc4iStatusReply>> command,
            Action<Sc4iStatusReply> onSuccess,
            string timeoutMessage,
            string failurePrefix)
        {
            if (busy || jogActive || sessionLost || !sc4iClient.IsConnected) return;
            busy = true;
            SetStatus(startMessage, UiTheme.Accent);
            RefreshUi();
            CancellationTokenSource cts = new CancellationTokenSource(timeoutMs);
            CancelAndDisposeCts(ref activeOpCts);
            activeOpCts = cts;
            try
            {
                Sc4iStatusReply reply = await command(cts.Token);
                if (reply != null) liveStatus = reply;
                if (!IsDisposed && !Disposing && onSuccess != null)
                    onSuccess(liveStatus);
            }
            catch (OperationCanceledException)
            {
                if (!formClosing && !IsDisposed && !Disposing && sc4iClient.IsConnected && ReferenceEquals(activeOpCts, cts))
                    SetStatus(timeoutMessage, UiTheme.Danger);
            }
            catch (Sc4iCommandException error)
            {
                liveStatus = error.Status;
                HandleSessionErrorIfNeeded(error);
                if (!IsDisposed && !Disposing && !sessionLost) SetStatus(error.Message, UiTheme.Danger);
            }
            catch (Exception error)
            {
                if (!IsDisposed && !Disposing) SetStatus(failurePrefix + error.Message, UiTheme.Danger);
            }
            finally
            {
                if (ReferenceEquals(activeOpCts, cts)) activeOpCts = null;
                cts.Dispose();
                busy = false;
                if (!IsDisposed && !Disposing) RefreshUi();
            }
        }

        private Task EnableSelectedAxisAsync()
        {
            int axis = selectedAxis;
            return RunExclusiveCommandAsync(
                "正在使能轴 " + (axis + 1) + "…",
                4000,
                ct => sc4iClient.EnableAxisAsync(axis, ct),
                reply =>
                {
                    string msg = reply.IsAxisEnabled(axis)
                        ? ("轴 " + (axis + 1) + " 已使能。")
                        : ("已发送轴 " + (axis + 1) + " 使能请求…");
                    SetStatus(msg, UiTheme.Success);
                },
                "使能请求超时。",
                "使能失败：");
        }

        private Task EnableAllAxesAsync()
        {
            return RunExclusiveCommandAsync(
                "正在使能并授权全部四轴…",
                5000,
                ct => sc4iClient.EnableAxesMaskAsync(0x0F, ct),
                reply =>
                {
                    SetStatus("已请求使能全部四轴。", UiTheme.Success);
                    FillTargetFromCurrentIfConnected();
                },
                "四轴使能超时。",
                "四轴使能失败：");
        }

        private Task MoveSelectedAxisAsync(int direction)
        {
            if (direction != 1 && direction != -1) return Task.FromResult(0);
            int axis = selectedAxis;
            double delta = direction * (double)jointPanel.StepRevolutions;
            double vel = (double)jointPanel.VelocityRevPerSec;
            return RunExclusiveCommandAsync(
                String.Format(CultureInfo.InvariantCulture, "轴 {0} 相对移动 {1:+0.00;-0.00} 圈…", axis + 1, delta),
                4000,
                ct => sc4iClient.MoveAxisRelativeAsync(axis, delta, vel, DefaultAccelerationRevPerSec2, ct),
                reply => SetStatus(
                    String.Format(CultureInfo.InvariantCulture, "轴 {0} 已发送移动 {1:+0.00;-0.00} 圈。", axis + 1, delta),
                    UiTheme.Ink),
                "移动请求超时。",
                "移动失败：");
        }

        private Task ClearFaultsAsync()
        {
            return RunExclusiveCommandAsync(
                "正在清除故障与停机锁存…",
                5000,
                ct => sc4iClient.ClearFaultsAndLatchAsync(ct),
                reply => SetStatus("故障已清除，可重新使能轴。", UiTheme.Success),
                "清故障超时。",
                "清故障失败：");
        }

        private async Task MoveCartesianStepAsync(CartesianStepAxis stepAxis)
        {
            int[] counts;
            if (liveStatus == null || !liveStatus.TryExtractLiveCounts(out counts)) return;
            ScaraJointPose currentJoints;
            ScaraCartesianPose cur = ScaraKinematics.ForwardFromCounts(calibration, counts, out currentJoints);
            CartesianTarget target = CartesianMotionPlanner.BuildRelativeStepTarget(
                cur, stepAxis, cartesianPanel.StepMm, cartesianPanel.StepDeg, cartesianPanel.KeepWorldR);
            await ExecuteCartesianMoveAsync(target);
        }

        private async Task ExecuteCartesianMoveAsync(CartesianTarget target)
        {
            if (busy || jogActive || sessionLost || !sc4iClient.IsConnected || target == null) return;

            CartesianMovePlan plan = CartesianMotionPlanner.PlanMove(
                calibration, liveStatus, target, cartesianPanel.ElbowPreference);
            if (!plan.Success)
            {
                SetStatus(plan.Error, UiTheme.Danger);
                return;
            }
            if (plan.IsNoOp)
            {
                SetStatus("当前已处于目标位置附近，无需移动。", UiTheme.Muted);
                return;
            }

            double vel = cartesianPanel.VelocityRevPerSec;
            string actionLabel = target.Label ?? "笛卡尔运动";

            await RunExclusiveCommandAsync(
                "正在执行 " + actionLabel + "…",
                45000,
                ct => CartesianMotionPlanner.ExecutePlanAsync(
                    sc4iClient,
                    plan,
                    vel,
                    DefaultAccelerationRevPerSec2,
                    actionLabel,
                    OnCartesianProgress,
                    ct),
                reply =>
                {
                    FillTargetFromCurrentIfConnected();
                    SetStatus(actionLabel + " 已到位。", UiTheme.Success);
                },
                "笛卡尔运动已中止或超时。",
                "笛卡尔运动失败：");
        }

        private void OnCartesianProgress(CartesianStepProgress progress)
        {
            if (progress == null || IsDisposed || Disposing) return;
            Action apply = delegate
            {
                if (IsDisposed || Disposing || !sc4iClient.IsConnected) return;
                if (progress.LatestStatus != null) liveStatus = progress.LatestStatus;
                if (!String.IsNullOrEmpty(progress.Message)) SetStatus(progress.Message, UiTheme.Accent);
                RefreshUi();
            };

            if (InvokeRequired)
            {
                try { BeginInvoke(apply); } catch { }
            }
            else
            {
                apply();
            }
        }

        private async Task StartJogAsync(int direction)
        {
            if (busy || jogActive || sessionLost || !sc4iClient.IsConnected || (direction != 1 && direction != -1)) return;
            int axis = selectedAxis;
            double vel = direction * Math.Min((double)jointPanel.VelocityRevPerSec, Sc4iClient.MaxSafeJogVelocityRevPerSec);
            jogActive = true;
            int myGen = ++jogGeneration;
            SetStatus(String.Format(CultureInfo.InvariantCulture, "轴 {0} 点动中（松开即停）…", axis + 1), UiTheme.Accent);
            CancellationTokenSource cts = new CancellationTokenSource(2000);
            CancelAndDisposeCts(ref jogCts);
            jogCts = cts;
            try
            {
                Sc4iStatusReply reply = await sc4iClient.StartJogAsync(axis, vel, cts.Token);
                if (IsDisposed || Disposing || myGen != jogGeneration || !jogActive) return;
                liveStatus = reply;
                jogTimer.Start();
                RefreshUi();
            }
            catch (OperationCanceledException)
            {
                if (myGen == jogGeneration && jogActive)
                {
                    jogActive = false;
                    if (!IsDisposed && !Disposing) { SetStatus("点动启动超时。", UiTheme.Danger); RefreshUi(); }
                }
            }
            catch (Sc4iCommandException error)
            {
                if (myGen == jogGeneration)
                {
                    jogActive = false;
                    liveStatus = error.Status;
                    HandleSessionErrorIfNeeded(error);
                    if (!IsDisposed && !Disposing)
                    {
                        if (!sessionLost) SetStatus(error.Message, UiTheme.Danger);
                        RefreshUi();
                    }
                }
            }
            catch (Exception error)
            {
                if (myGen == jogGeneration)
                {
                    jogActive = false;
                    if (!IsDisposed && !Disposing) { SetStatus("点动失败：" + error.Message, UiTheme.Danger); RefreshUi(); }
                }
            }
            finally
            {
                if (ReferenceEquals(jogCts, cts)) jogCts = null;
                cts.Dispose();
            }
        }

        private async Task SendJogKeepaliveAsync()
        {
            if (!jogActive || jogKeepaliveInFlight || sessionLost || !sc4iClient.IsConnected) return;
            jogKeepaliveInFlight = true;
            int myGen = jogGeneration;
            CancellationTokenSource cts = new CancellationTokenSource(280);
            CancelAndDisposeCts(ref jogCts);
            jogCts = cts;
            try
            {
                Sc4iStatusReply reply = await sc4iClient.SendJogKeepaliveAsync(cts.Token);
                if (IsDisposed || Disposing || myGen != jogGeneration || !sc4iClient.IsConnected) return;
                liveStatus = reply;
                heartbeatFailCount = 0;
                if (reply.ErrorCode != 0 || reply.LatchedFault != 0 || reply.EstopFlag != 0)
                {
                    jogTimer.Stop();
                    jogActive = false;
                    jogGeneration++;
                    if (reply.ErrorCode == 0x36 || reply.ErrorCode == 0x37)
                        MarkSessionLost();
                    else if (reply.ErrorCode != 0)
                        SetStatus("点动已停止：" + Sc4iProtocol.FormatErrorCode(reply.ErrorCode), UiTheme.Danger);
                }
                RefreshUi();
            }
            catch (OperationCanceledException)
            {
                if (myGen == jogGeneration && jogActive && sc4iClient.IsConnected)
                {
                    jogTimer.Stop();
                    jogActive = false;
                    jogGeneration++;
                    if (!IsDisposed && !Disposing) { SetStatus("点动保活超时，已请求停止保持。", UiTheme.Danger); RefreshUi(); }
                    await TriggerStopHoldAsync("点动保活超时，已停止保持");
                }
            }
            catch (Exception error)
            {
                if (myGen == jogGeneration && jogActive)
                {
                    jogTimer.Stop();
                    jogActive = false;
                    jogGeneration++;
                    if (!IsDisposed && !Disposing) { SetStatus("点动保活异常：" + error.Message, UiTheme.Danger); RefreshUi(); }
                    if (sc4iClient.IsConnected)
                        await TriggerStopHoldAsync("点动通信异常，已停止保持");
                }
            }
            finally
            {
                if (ReferenceEquals(jogCts, cts)) jogCts = null;
                cts.Dispose();
                jogKeepaliveInFlight = false;
            }
        }

        private async Task StopJogAsync(string message)
        {
            if (!jogActive) return;
            jogTimer.Stop();
            jogActive = false;
            jogGeneration++;
            CancelAndDisposeCts(ref jogCts);
            await TriggerStopHoldAsync(message);
        }

        private async Task TriggerStopHoldAsync(string message)
        {
            if (!sc4iClient.IsConnected) return;
            jogTimer.Stop();
            jogActive = false;
            jogGeneration++;
            CancelAllInFlightOps();
            try
            {
                Sc4iStatusReply reply;
                using (CancellationTokenSource cts = new CancellationTokenSource(2500))
                {
                    reply = await sc4iClient.StopHoldAsync(cts.Token);
                }
                if (IsDisposed || Disposing || !sc4iClient.IsConnected) return;
                liveStatus = reply;
                if (reply.ErrorCode != 0)
                {
                    if (reply.ErrorCode == 0x36 || reply.ErrorCode == 0x37)
                        MarkSessionLost();
                    else
                        SetStatus("停止保持返回错误：" + Sc4iProtocol.FormatErrorCode(reply.ErrorCode), UiTheme.Danger);
                }
                else
                {
                    SetStatus(message, UiTheme.Accent);
                }
            }
            catch (Exception error)
            {
                if (!IsDisposed && !Disposing) SetStatus("停止失败：" + error.Message, UiTheme.Danger);
            }
            finally
            {
                if (!IsDisposed && !Disposing) RefreshUi();
            }
        }

        private async Task PollStatusAsync()
        {
            if (!sc4iClient.IsConnected || pollInFlight || busy || jogActive || sessionLost) return;
            pollInFlight = true;
            CancellationTokenSource cts = new CancellationTokenSource(650);
            CancelAndDisposeCts(ref pollCts);
            pollCts = cts;
            try
            {
                Sc4iStatusReply reply = await sc4iClient.SendHeartbeatAsync(cts.Token);
                if (IsDisposed || Disposing || !sc4iClient.IsConnected) return;
                liveStatus = reply;
                bool recovered = heartbeatFailCount > 0;
                heartbeatFailCount = 0;
                if (recovered) SetStatus("通信已恢复。", UiTheme.Success);
                RefreshUi();
            }
            catch (OperationCanceledException)
            {
                if (IsDisposed || Disposing || !sc4iClient.IsConnected || busy || jogActive) return;
                if (!ReferenceEquals(pollCts, cts)) return;
                heartbeatFailCount++;
                if (heartbeatFailCount == 1) SetStatus("通信超时，正在等待恢复…", UiTheme.Danger);
                RefreshUi();
            }
            catch (Sc4iCommandException error)
            {
                if (IsDisposed || Disposing || !sc4iClient.IsConnected) return;
                liveStatus = error.Status;
                if (error.ErrorCode == 0x36 || error.ErrorCode == 0x37)
                {
                    MarkSessionLost();
                }
                else
                {
                    heartbeatFailCount++;
                    if (heartbeatFailCount == 1) SetStatus(error.Message, UiTheme.Danger);
                }
                RefreshUi();
            }
            catch
            {
                if (IsDisposed || Disposing || !sc4iClient.IsConnected) return;
                heartbeatFailCount++;
                if (heartbeatFailCount == 1) SetStatus("通信超时，正在等待恢复…", UiTheme.Danger);
                RefreshUi();
            }
            finally
            {
                if (ReferenceEquals(pollCts, cts)) pollCts = null;
                cts.Dispose();
                pollInFlight = false;
            }
        }

        private void HandleSessionErrorIfNeeded(Sc4iCommandException error)
        {
            if (error != null && (error.ErrorCode == 0x36 || error.ErrorCode == 0x37))
                MarkSessionLost();
        }

        private void MarkSessionLost()
        {
            sessionLost = true;
            pollTimer.Stop();
            jogTimer.Stop();
            jogActive = false;
            jogGeneration++;
            if (!IsDisposed && !Disposing)
                SetStatus("会话已失效或控制卡已重启，请点击“断开”后重新连接。", UiTheme.Danger);
        }

        private static void CancelAndDisposeCts(ref CancellationTokenSource field)
        {
            CancellationTokenSource old = field;
            field = null;
            if (old != null)
            {
                try { old.Cancel(); } catch { }
                try { old.Dispose(); } catch { }
            }
        }

        private void CancelAllInFlightOps()
        {
            CancelAndDisposeCts(ref pollCts);
            CancelAndDisposeCts(ref jogCts);
            CancelAndDisposeCts(ref activeOpCts);
        }
    }
}
