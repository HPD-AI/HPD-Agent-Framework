#r "nuget: System.Drawing.Common, 8.0.0"
using System;
using System.IO;
using System.Drawing;

// Read the cat image
var imagePath = "Cat_November_2010-1a.jpg";
var imageBytes = File.ReadAllBytes(imagePath);
Console.WriteLine($"Original file size: {imageBytes.Length} bytes");

// Encode to base64 (what your app does)
var base64 = Convert.ToBase64String(imageBytes);
Console.WriteLine($"Base64 length: {base64.Length} characters");

// Create data URI (what gets sent)
var dataUri = $"data:image/jpeg;base64,{base64}";
Console.WriteLine($"Data URI length: {dataUri.Length} characters");

// Decode back to verify integrity
var base64Part = dataUri.Substring(dataUri.IndexOf(",") + 1);
var decodedBytes = Convert.FromBase64String(base64Part);
Console.WriteLine($"Decoded size: {decodedBytes.Length} bytes");

// Check if identical
bool identical = imageBytes.SequenceEqual(decodedBytes);
Console.WriteLine($"\nIntegrity check: {(identical ? "✓ PASSED" : "✗ FAILED")}");

if (identical)
{
    Console.WriteLine("Base64 encoding/decoding is working correctly.");
    
    // Try to decode the image to verify it's a valid JPEG
    try
    {
        using (var ms = new MemoryStream(decodedBytes))
        using (var img = Image.FromStream(ms))
        {
            Console.WriteLine($"Image dimensions: {img.Width}x{img.Height}");
            Console.WriteLine($"Image format: {img.RawFormat}");
            Console.WriteLine("\n✓ Image can be decoded successfully");
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"\n✗ Image decoding failed: {ex.Message}");
    }
}
else
{
    Console.WriteLine("WARNING: Base64 corruption detected!");
}
