"""Offline source for the embedded synthetic SDK smoke PNG; not needed by tests."""
from pathlib import Path
from PIL import Image, ImageDraw, ImageFont

output = Path("tests/Iris.Platform.Windows.Tests/Ocr/Fixtures/hello.png")
output.parent.mkdir(parents=True, exist_ok=True)
canvas = Image.new("RGB", (640, 160), "white")
ImageDraw.Draw(canvas).text((25, 25), "HELLO 123",
    font=ImageFont.truetype("C:/Windows/Fonts/arial.ttf", 72), fill="black")
canvas.save(output)
