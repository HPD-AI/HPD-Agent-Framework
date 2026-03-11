using Helium.Primitives;
using Helium.Algebra;

namespace Helium.Native;

/// <summary>
/// BLAS-backed fast paths for Matrix&lt;Double&gt; and Matrix&lt;Float&gt; multiplication.
/// Replaces the generic O(n³) triple-loop with cblas_dgemm / cblas_sgemm.
///
/// On macOS Apple Silicon: backed by AMX via Accelerate — 100-1000× faster than
/// the naive loop for large matrices.
///
/// The generic Matrix&lt;R&gt; type is untouched. These extensions only apply when R is
/// Double or Float, selected by the C# 14 extension block on the concrete type.
/// </summary>
public static class MatrixBlasExtensions
{
    extension(Matrix<Double> _)
    {
        public static Matrix<Double> operator *(Matrix<Double> a, Matrix<Double> b)
            => BlasInterop.Dgemm(a, b);
    }

    extension(Matrix<Float> _)
    {
        public static Matrix<Float> operator *(Matrix<Float> a, Matrix<Float> b)
            => BlasInterop.Sgemm(a, b);
    }
}
