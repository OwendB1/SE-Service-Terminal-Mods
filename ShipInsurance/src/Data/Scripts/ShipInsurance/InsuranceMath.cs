using System;

namespace ShipInsurance
{
    internal static class InsuranceMath
    {
        public static double Clamp01(double value)
        {
            if (value < 0.0) return 0.0;
            if (value > 1.0) return 1.0;
            return value;
        }

        public static long Scale(long value, double factor)
        {
            if (value <= 0 || factor <= 0.0) return 0;
            double result = Math.Ceiling(value * factor);
            return result >= long.MaxValue ? long.MaxValue : (long)result;
        }

        public static long Add(long left, long right)
        {
            if (right <= 0) return left;
            return left > long.MaxValue - right ? long.MaxValue : left + right;
        }

        public static long Multiply(long left, long right)
        {
            if (left <= 0 || right <= 0) return 0;
            return left > long.MaxValue / right ? long.MaxValue : left * right;
        }

        public static long EnrollmentFee(long value, long flatFee, double valueFraction)
        {
            return EnrollmentFee(value, flatFee, valueFraction, 0.0);
        }

        public static long EnrollmentFee(long value, long flatFee, double valueFraction,
            double discount)
        {
            long valueFee = Scale(value, Math.Max(0.0, valueFraction));
            return Math.Max(Math.Max(0, flatFee), Scale(valueFee, 1.0 - Clamp01(discount)));
        }

        public static long CancellationRefund(long enrollmentCost, double refundFraction)
        {
            if (enrollmentCost <= 0 || refundFraction <= 0.0) return 0;
            double result = Math.Floor(enrollmentCost * Clamp01(refundFraction));
            return result >= long.MaxValue ? long.MaxValue : (long)result;
        }

        public static long ClaimFee(long lossValue, double valueFraction, long minimumFee)
        {
            return ClaimFee(lossValue, valueFraction, minimumFee, 0.0);
        }

        public static long ClaimFee(long lossValue, double valueFraction, long minimumFee,
            double discount)
        {
            if (lossValue <= 0) return 0;
            long valueFee = Scale(lossValue, Math.Max(0.0, valueFraction));
            return Math.Max(Math.Max(0, minimumFee), Scale(valueFee, 1.0 - Clamp01(discount)));
        }

        public static double ReputationDiscount(int reputation, int friendlyMin, int friendlyMax,
            double maximumDiscount)
        {
            if (reputation <= friendlyMin || friendlyMax <= friendlyMin) return 0.0;
            double progress = ((double)reputation - friendlyMin) /
                              ((double)friendlyMax - friendlyMin);
            return Clamp01(progress) * Clamp01(maximumDiscount);
        }

        public static double Ratio(long part, long whole)
        {
            return whole <= 0 ? 0.0 : Math.Min(1.0, Math.Max(0.0, (double)part / whole));
        }

        public static bool RequiresRecovery(bool totalLoss, bool missingGrid, double lossRatio)
        {
            return totalLoss || missingGrid || lossRatio > 0.8;
        }

        public static long RemoteRecoveryFee(double distanceMeters, long feePerKilometer)
        {
            if (feePerKilometer <= 0) return 0;
            double billableKilometers = Math.Max(1.0, Math.Max(0.0, distanceMeters) / 1000.0);
            double result = Math.Ceiling(billableKilometers * feePerKilometer);
            return result >= long.MaxValue ? long.MaxValue : (long)result;
        }

        public static long RemoteRecoveryCooldownSeconds(double distanceMeters, double secondsPerKilometer,
            int minimumSeconds, int maximumSeconds)
        {
            long minimum = Math.Max(0, minimumSeconds);
            long maximum = Math.Max(minimum, maximumSeconds);
            double calculated = Math.Ceiling(Math.Max(0.0, distanceMeters) / 1000.0 *
                                             Math.Max(0.0, secondsPerKilometer));
            if (calculated >= maximum) return maximum;
            return Math.Max(minimum, (long)calculated);
        }

        public static long RemainingSeconds(long readyUtcTicks, long nowUtcTicks)
        {
            if (readyUtcTicks <= nowUtcTicks) return 0;
            long ticks = readyUtcTicks - nowUtcTicks;
            return ticks / TimeSpan.TicksPerSecond + (ticks % TimeSpan.TicksPerSecond == 0 ? 0 : 1);
        }

        public static long ExpediteFee(long remainingSeconds, long costPerSecond)
        {
            return Multiply(remainingSeconds, costPerSecond);
        }

        public static long ExpediteReadyUtcTicks(long nowUtcTicks, long readyUtcTicks, double factor)
        {
            if (readyUtcTicks <= nowUtcTicks) return nowUtcTicks;
            long remainingTicks = readyUtcTicks - nowUtcTicks;
            long reducedTicks = Scale(remainingTicks, Clamp01(factor));
            return Add(nowUtcTicks, reducedTicks);
        }

        public static long InsuranceCooldownSeconds(long serviceCost, long creditsPerSecond,
            int minimumSeconds, int maximumSeconds)
        {
            if (creditsPerSecond <= 0) return 0;
            long minimum = Math.Max(0, minimumSeconds);
            long maximum = Math.Max(minimum, maximumSeconds);
            long cost = Math.Max(0, serviceCost);
            long calculated = cost / creditsPerSecond;
            if (cost % creditsPerSecond != 0) calculated++;
            return Math.Min(maximum, Math.Max(minimum, calculated));
        }

#if DEBUG
        public static void SelfTest()
        {
            if (EnrollmentFee(1000, 100, 0.5) != 500) throw new InvalidOperationException("Enrollment fee self-test failed.");
            if (EnrollmentFee(1000, 100, 0.5, 0.1) != 450) throw new InvalidOperationException("Enrollment discount self-test failed.");
            if (EnrollmentFee(100, 100, 0.5, 0.5) != 100) throw new InvalidOperationException("Enrollment floor self-test failed.");
            if (CancellationRefund(1001, 0.5) != 500) throw new InvalidOperationException("Cancellation refund self-test failed.");
            if (CancellationRefund(1000, 2.0) != 1000) throw new InvalidOperationException("Cancellation refund clamp self-test failed.");
            if (ClaimFee(250, 1.0, 0) != 250) throw new InvalidOperationException("Claim fee self-test failed.");
            if (ClaimFee(250, 1.0, 100, 0.1) != 225) throw new InvalidOperationException("Claim discount self-test failed.");
            if (ClaimFee(50, 1.0, 100, 0.5) != 100) throw new InvalidOperationException("Claim floor self-test failed.");
            if (Math.Abs(ReputationDiscount(1000, 500, 1500, 0.1) - 0.05) > 0.00001) throw new InvalidOperationException("Reputation discount self-test failed.");
            if (ReputationDiscount(500, 500, 1500, 0.1) != 0.0) throw new InvalidOperationException("Reputation minimum self-test failed.");
            if (Math.Abs(ReputationDiscount(2000, 500, 1500, 0.1) - 0.1) > 0.00001) throw new InvalidOperationException("Reputation maximum self-test failed.");
            if (Math.Abs(Ratio(25, 100) - 0.25) > 0.00001) throw new InvalidOperationException("Loss ratio self-test failed.");
            if (RequiresRecovery(false, false, 0.8)) throw new InvalidOperationException("Recovery threshold self-test failed.");
            if (!RequiresRecovery(false, false, 0.80001)) throw new InvalidOperationException("Recovery threshold self-test failed.");
            if (Add(long.MaxValue - 2, 10) != long.MaxValue) throw new InvalidOperationException("Overflow self-test failed.");
            if (Multiply(long.MaxValue, 2) != long.MaxValue) throw new InvalidOperationException("Multiply self-test failed.");
            if (RemoteRecoveryFee(1250.0, 1000) != 1250) throw new InvalidOperationException("Remote fee self-test failed.");
            if (RemoteRecoveryFee(0.0, 1000) != 1000) throw new InvalidOperationException("Remote minimum fee self-test failed.");
            if (RemoteRecoveryCooldownSeconds(10000.0, 1.0, 60, 3600) != 60) throw new InvalidOperationException("Cooldown minimum self-test failed.");
            if (RemoteRecoveryCooldownSeconds(10000000.0, 1.0, 60, 3600) != 3600) throw new InvalidOperationException("Cooldown maximum self-test failed.");
            long now = 100 * TimeSpan.TicksPerSecond;
            long ready = 200 * TimeSpan.TicksPerSecond;
            if (RemainingSeconds(ready, now) != 100) throw new InvalidOperationException("Remaining time self-test failed.");
            if (ExpediteFee(100, 1000) != 100000) throw new InvalidOperationException("Expedite fee self-test failed.");
            if (ExpediteReadyUtcTicks(now, ready, 0.5) != 150 * TimeSpan.TicksPerSecond) throw new InvalidOperationException("Expedite time self-test failed.");
            if (InsuranceCooldownSeconds(250000, 1000, 60, 86400) != 250) throw new InvalidOperationException("Insurance cooldown self-test failed.");
            if (InsuranceCooldownSeconds(0, 1000, 60, 86400) != 60) throw new InvalidOperationException("Insurance cooldown minimum self-test failed.");
            if (InsuranceCooldownSeconds(long.MaxValue, 1000, 60, 86400) != 86400) throw new InvalidOperationException("Insurance cooldown maximum self-test failed.");
            if (InsuranceCooldownSeconds(1000, 0, 60, 86400) != 0) throw new InvalidOperationException("Insurance cooldown disabled self-test failed.");
        }
#endif
    }
}
