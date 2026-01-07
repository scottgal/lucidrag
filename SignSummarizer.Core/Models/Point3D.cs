namespace SignSummarizer.Models;

public readonly record struct Point3D(float X, float Y, float Z)
{
    public static readonly Point3D Zero = new(0, 0, 0);
    
    public float DistanceTo(Point3D other)
    {
        var dx = X - other.X;
        var dy = Y - other.Y;
        var dz = Z - other.Z;
        return MathF.Sqrt(dx * dx + dy * dy + dz * dz);
    }
    
    public Point3D Normalize()
    {
        var magnitude = MathF.Sqrt(X * X + Y * Y + Z * Z);
        return magnitude > 0 ? this with { X = X / magnitude, Y = Y / magnitude, Z = Z / magnitude } : Zero;
    }
}