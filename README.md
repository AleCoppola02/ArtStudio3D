# Art Studio

**Art Studio** is a  digital painting software built in the Unity engine. 

---

##  Key Features

* **Catmull-Rom Spline Interpolation:** Smooths raw input data (mouse, stylus, or touch) in real time using Catmull-Rom curves.
* **Brush Dynamics:** Control over core painting properties:
  * **Size:** Adjust brush tip dimensions.
  * **Opacity:** Manage overall stroke transparency.
  * **Flow:** Control the rate of paint accumulation per stroke.
* **Sparse Virtual Texturing (SVT):** Virtual tiling engine capable of handling large canvases—such as **20,000 × 20,000 pixels** without exhausting system RAM or VRAM.

---

##  Installation

This project uses Unity version 6000.3.8f1. To run it on your machine, clone the project with the command 
```
git clone https://github.com/AleCoppola02/ArtStudio3D
```

Alternatively **[Click here to download the v1.0 release build](https://github.com/AleCoppola02/ArtStudio3D/releases/tag/1.0)**

--- 

## Using Art Studio
Use the left mouse button to draw, and the middle mouse button to pan the camera. You can zoom in and out using the scroll wheel. Since this is a prototype, you may experience issues if you create very high resolution canvases. Changing brush settings (opacity, flow, size, color) while the software is still rendering previous inputs will cause an error. Furthermore, Art Studio may not work properly on all graphics cards.
