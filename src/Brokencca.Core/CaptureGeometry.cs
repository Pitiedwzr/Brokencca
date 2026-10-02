namespace Brokencca.Core;

public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public int Right => checked(X + Width);
    public int Bottom => checked(Y + Height);
    public bool IsPositive => Width > 0 && Height > 0;
    public bool Contains(double x, double y) => x >= X && y >= Y && x < Right && y < Bottom;
}

public readonly record struct NormalizedCrop(double X, double Y, double Width, double Height)
{
    public static NormalizedCrop Full => new(0, 0, 1, 1);
    public void Validate()
    {
        if (!double.IsFinite(X + Y + Width + Height) || X < 0 || Y < 0 || Width <= 0 || Height <= 0 || X + Width > 1 || Y + Height > 1)
            throw new ArgumentException("Crop must be a finite, positive rectangle inside [0,1].");
    }
    public PixelRect ToPixels(PixelRect client)
    {
        Validate();
        if (!client.IsPositive) throw new ArgumentException("Client rectangle is empty.");
        int left = Math.Clamp((int)Math.Floor(X * client.Width), 0, client.Width);
        int top = Math.Clamp((int)Math.Floor(Y * client.Height), 0, client.Height);
        int right = Math.Clamp((int)Math.Ceiling((X + Width) * client.Width), left, client.Width);
        int bottom = Math.Clamp((int)Math.Ceiling((Y + Height) * client.Height), top, client.Height);
        return new(client.X + left, client.Y + top, right - left, bottom - top);
    }
}

/// <summary>One uniform transform for image, circle guide, and future device touch coordinates.</summary>
public readonly record struct CaptureViewport(PixelRect Source, int OutputWidth, int OutputHeight)
{
    public double Scale => Math.Min((double)OutputWidth / Source.Width, (double)OutputHeight / Source.Height);
    public double Left => (OutputWidth - Source.Width * Scale) / 2;
    public double Top => (OutputHeight - Source.Height * Scale) / 2;
    public (double X, double Y) ToDisplay(double x, double y) => (Left + (x - Source.X) * Scale, Top + (y - Source.Y) * Scale);
    public bool TryToSource(double x, double y, out double sourceX, out double sourceY)
    {
        sourceX = Source.X + (x - Left) / Scale;
        sourceY = Source.Y + (y - Top) / Scale;
        return Source.IsPositive && OutputWidth > 0 && OutputHeight > 0 && Source.Contains(sourceX, sourceY);
    }
}

public sealed record PlayfieldCircle(double CenterX, double CenterY, double Radius, string Provenance)
{
    public void Validate()
    {
        if (!double.IsFinite(CenterX + CenterY + Radius) || CenterX < 0 || CenterX > 1 || CenterY < 0 || CenterY > 1 || Radius <= 0 || Radius > 1)
            throw new ArgumentException("Invalid normalized playfield circle.");
    }
    public (double X, double Y, double R) ToPixels(int width, int height) => (CenterX * width, CenterY * height, Radius * Math.Min(width, height));
    public static PlayfieldCircle TouccaReference(int clientWidth, int clientHeight, int outerRightFromClient, int outerBottomFromClient)
    {
        int w = outerRightFromClient - 10, h = (int)((outerBottomFromClient - 10) * .938);
        if (clientWidth <= 0 || clientHeight <= 0 || w <= 0 || h <= 0) throw new ArgumentException("Empty toucca reference bounds.");
        var circle = new PlayfieldCircle(w / (2.0 * clientWidth), h / (2.0 * clientHeight), Math.Min(w, h) / (2.0 * Math.Min(clientWidth, clientHeight)), "toucca-reference");
        circle.Validate();
        return circle;
    }
}

public sealed record CaptureProfile(int Version, int SourceWidth, int SourceHeight, uint Dpi, NormalizedCrop Crop, PlayfieldCircle Circle, bool Confirmed)
{
    public void ValidateFor(int width, int height)
    {
        Crop.Validate(); Circle.Validate();
        if (Version != 1 || SourceWidth <= 0 || SourceHeight <= 0 || !Confirmed) throw new ArgumentException("Profile must be a confirmed version-1 calibration.");
        if (width <= 0 || height <= 0 || Math.Abs((double)SourceWidth / SourceHeight - (double)width / height) > .005)
            throw new ArgumentException("Source aspect ratio changed; recalibrate this profile.");
    }
}

/// <summary>Three consistent observations are required. All-black/invalid observations break the streak.</summary>
public sealed class CircleConsensus
{
    private PlayfieldCircle? previous;
    private int count;
    public PlayfieldCircle? Observe(PlayfieldCircle? candidate)
    {
        if (candidate is null) { previous = null; count = 0; return null; }
        bool matches = previous is not null && Math.Abs(candidate.CenterX - previous.CenterX) < .008 && Math.Abs(candidate.CenterY - previous.CenterY) < .008 && Math.Abs(candidate.Radius - previous.Radius) < .008;
        count = matches ? count + 1 : 1;
        previous = candidate;
        return count >= 3 ? candidate : null;
    }
}
