namespace NetRatel.Client.Service.Auth;

public static class EnrollmentStartupPolicy
{
    public const int ConfigurationErrorExitCode = 78;

    public static bool ShouldPromptForEnrollment(bool serviceMode, string? enrollmentCode)
        => !serviceMode && string.IsNullOrWhiteSpace(enrollmentCode);

    public static int GetMissingEnrollmentExitCode(bool serviceMode)
        => serviceMode ? ConfigurationErrorExitCode : 11;
}
