#if SDK_NEGATIVE_TEST
using WowDi.ModelingSdk;
using WowDi.Udir;

namespace WowDi.Samples;

// Opt-in compiler-negative fixture. Normal sample compilation never includes this invalid call.
internal static class CompileRejection
{
    internal static void InvalidAddition(ModelBuilder builder, QuantityExpression<ExampleModels.Kilogram> mass,
        QuantityExpression<ExampleModels.Dollar> money, SourceSpan span)
    {
        _ = builder.Add(mass, money, span);
    }
}
#endif
