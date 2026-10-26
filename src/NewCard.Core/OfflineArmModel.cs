using System;

namespace NewCard.Core
{
    /// <summary>Four synthetic axes. Units and limits have no physical meaning.</summary>
    public sealed class OfflineArmModel
    {
        private readonly double[] positions = new double[4];
        private readonly double[] targets = new double[4];
        private double speed;
        public int AxisCount { get { return 4; } }
        public bool IsMoving
        {
            get { for (int i = 0; i < AxisCount; i++) if (IsAxisMoving(i)) return true; return false; }
        }

        private void ValidateAxis(int axis)
        {
            if (axis < 0 || axis >= AxisCount) throw new ArgumentOutOfRangeException("axis");
        }
        private static bool Finite(double value) { return !double.IsNaN(value) && !double.IsInfinity(value); }
        public double GetPosition(int axis) { ValidateAxis(axis); return positions[axis]; }
        public double GetTarget(int axis) { ValidateAxis(axis); return targets[axis]; }
        public bool IsAxisMoving(int axis) { ValidateAxis(axis); return positions[axis] != targets[axis]; }

        public void MoveRelative(int axis, double delta, double unitsPerSecond)
        {
            ValidateAxis(axis);
            if (!Finite(delta) || delta == 0 || Math.Abs(delta) > 1000)
                throw new ArgumentException("位移必须是非零有限数，绝对值不超过 1000 演示单位。", "delta");
            if (!Finite(unitsPerSecond) || unitsPerSecond <= 0 || unitsPerSecond > 100)
                throw new ArgumentException("速度必须大于 0 且不超过 100 演示单位/秒。", "unitsPerSecond");
            if (IsMoving) throw new InvalidOperationException("请先停止或等待当前模拟运动完成。");
            double target = positions[axis] + delta;
            if (Math.Abs(target) > 1000) throw new ArgumentException("模拟目标超出 ±1000 演示单位。", "delta");
            targets[axis] = target;
            speed = unitsPerSecond;
        }

        public void Advance(double seconds)
        {
            if (!Finite(seconds) || seconds < 0) throw new ArgumentOutOfRangeException("seconds");
            for (int i = 0; i < AxisCount; i++)
            {
                double remaining = targets[i] - positions[i];
                double distance = Math.Min(Math.Abs(remaining), speed * seconds);
                positions[i] = distance >= Math.Abs(remaining) ? targets[i] : positions[i] + Math.Sign(remaining) * distance;
            }
        }
        public void StopAll() { Array.Copy(positions, targets, AxisCount); }
        public void Reset() { StopAll(); Array.Clear(positions, 0, AxisCount); Array.Clear(targets, 0, AxisCount); }
    }
}
