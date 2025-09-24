namespace Silica.Certificates.Abstractions
{
    /// <summary>
    /// Options for certificate providers. Contract-first and minimal for operational tuning.
    /// </summary>
    public sealed class CertificateProviderOptions
    {
        /// <summary>
        /// Lifetime in days for generated ephemeral certificates. Defaults to 1 day if not set or invalid.
        /// Upper bound enforced at 3650 days.
        /// </summary>
        public double LifetimeDays { get; set; } = 1.0;

        /// <summary>
        /// Minutes of negative skew to apply to NotBefore to tolerate minor clock differences.
        /// Defaults to 5 minutes.
        /// </summary>
        public int NotBeforeSkewMinutes { get; set; } = 5;

        /// <summary>
        /// Whether to add Environment.MachineName as a SAN DNS entry. Defaults to true.
        /// </summary>
        public bool IncludeMachineNameSan { get; set; } = true;

        /// <summary>
        /// Key algorithm for generated ephemeral certificates: "rsa" or "ecdsa". Defaults to "rsa".
        /// </summary>
        public string KeyAlgorithm { get; set; } = "rsa";

        /// <summary>
        /// RSA key size when KeyAlgorithm="rsa". Defaults to 2048. Range: 2048–4096.
        /// </summary>
        public int RsaKeySize { get; set; } = 2048;
    }
}
