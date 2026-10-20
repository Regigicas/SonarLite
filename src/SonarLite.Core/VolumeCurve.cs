namespace SonarLite.Core;

/// <summary>
/// Converts Sonar's channel volume (the scalar volume of its virtual endpoints) to dB.
/// </summary>
/// <remarks>
/// Sonar sets each channel with IAudioEndpointVolume.SetMasterVolumeLevelScalar, so the real
/// attenuation follows Windows' audio-taper curve for a software volume range of [-96, 0] dB,
/// which is not a plain 20·log10. The table was measured on Windows 11 (GetMasterVolumeLevel
/// at each 1 % step) and matches the readings of all Sonar virtual devices.
/// </remarks>
public static class VolumeCurve
{
    private static readonly float[] DecibelsPerPercent =
    [
        -96.00f, -67.50f, -58.17f, -52.44f, -48.28f, -45.02f, -42.34f, -40.06f, -38.08f, -36.33f,
        -34.75f, -33.33f, -32.03f, -30.83f, -29.72f, -28.68f, -27.71f, -26.80f, -25.94f, -25.13f,
        -24.35f, -23.62f, -22.92f, -22.25f, -21.61f, -20.99f, -20.40f, -19.83f, -19.28f, -18.75f,
        -18.24f, -17.74f, -17.26f, -16.80f, -16.34f, -15.91f, -15.48f, -15.07f, -14.66f, -14.27f,
        -13.89f, -13.51f, -13.15f, -12.79f, -12.44f, -12.10f, -11.77f, -11.45f, -11.13f, -10.81f,
        -10.51f, -10.21f, -9.91f, -9.63f, -9.34f, -9.06f, -8.79f, -8.52f, -8.26f, -8.00f,
        -7.75f, -7.50f, -7.25f, -7.01f, -6.77f, -6.53f, -6.30f, -6.07f, -5.85f, -5.63f,
        -5.41f, -5.19f, -4.98f, -4.77f, -4.57f, -4.36f, -4.16f, -3.96f, -3.77f, -3.58f,
        -3.38f, -3.20f, -3.01f, -2.83f, -2.64f, -2.47f, -2.29f, -2.11f, -1.94f, -1.77f,
        -1.60f, -1.43f, -1.27f, -1.10f, -0.94f, -0.78f, -0.62f, -0.46f, -0.31f, -0.15f,
        0.00f,
    ];

    /// <param name="scalar">Volume in 0..1.</param>
    /// <returns>Attenuation in dB; negative infinity for silence.</returns>
    public static double ToDecibels(double scalar)
    {
        if (scalar <= 0)
            return double.NegativeInfinity;
        if (scalar >= 1)
            return 0;
        double position = scalar * 100;
        int index = (int)position;
        double fraction = position - index;
        return DecibelsPerPercent[index] + (DecibelsPerPercent[index + 1] - DecibelsPerPercent[index]) * fraction;
    }
}
