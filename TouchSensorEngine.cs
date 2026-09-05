using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using WpfMaiTouchEmulator.Managers;
using System.Numerics;
using System.Windows.Controls.Primitives;

namespace WpfMaiTouchEmulator;

public sealed class TouchSensorEngine
{
    // Precomputed geometry per sensor
    private readonly SensorGeometry[] _sensors;
    private readonly ushort[] _sensorIdToIndex; // TouchValue -> index
    
    // Per-pointer state
    private readonly ConcurrentDictionary<uint, PointerState> _pointers = new();
    
    // Hold counts per sensor
    private readonly int[] _holdCounts;
    
    // Configuration
    private double _touchRadius;
    private double _buttonRadius;
    
    public double GetTouchRadius() => _touchRadius;
    public double GetButtonRadius() => _buttonRadius;

    public IEnumerable<SensorGeometry> GetSensorPolygons() => _sensors;
    
    // Button sensor mask (A1-A8, D1-D8)
    private readonly ulong _buttonMask;
    
    // Callbacks
    public Action<TouchValue>? OnPress;
    public Action<TouchValue>? OnRelease;
    
    // Tracing
    public Action<string, string>? Trace;
    
    public TouchSensorEngine(
        IReadOnlyDictionary<TouchValue, Polygon> polygons,
        double touchRadius = 35,
        double buttonRadius = 25)
    {
        // Radii stored for settings sync only
        _touchRadius = touchRadius;
        _buttonRadius = buttonRadius;
        
        // Build sensor geometry
        var sensorList = new List<SensorGeometry>();
        var idToIndex = new Dictionary<TouchValue, int>();
        
        int index = 0;
        foreach (var kv in polygons)
        {
            var poly = kv.Value;
            double left = Canvas.GetLeft(poly); if (double.IsNaN(left)) left = 0;
            double top = Canvas.GetTop(poly); if (double.IsNaN(top)) top = 0;
            
            var pts = poly.Points;
            var absPts = new Point[pts.Count];
            double minX = double.MaxValue, maxX = double.MinValue, minY = double.MaxValue, maxY = double.MinValue;
            
            for (int i = 0; i < pts.Count; i++)
            {
                double x = left + pts[i].X;
                double y = top + pts[i].Y;
                absPts[i] = new Point(x, y);
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
            }
            
            bool isButton = IsButtonSensor(kv.Key);
            sensorList.Add(new SensorGeometry
            {
                Value = kv.Key,
                Points = absPts,
                MinX = minX, MaxX = maxX, MinY = minY, MaxY = maxY,
                IsButton = isButton
            });
            idToIndex[kv.Key] = index++;
        }
        
        _sensors = sensorList.ToArray();
        _sensorIdToIndex = new ushort[64]; // TouchValue max bit = 33
        foreach (var kv in idToIndex)
            _sensorIdToIndex[GetBitIndex(kv.Key)] = (ushort)kv.Value;
        
        _holdCounts = new int[_sensors.Length];
        
        // Button mask: A1-A8 (bits 0-7) + D1-D8 (bits 18-25)
        _buttonMask = 0xFFUL | (0xFFUL << 18);
    }
    
    private static bool IsButtonSensor(TouchValue tv) => 
        (tv >= TouchValue.A1 && tv <= TouchValue.A8) || 
        (tv >= TouchValue.D1 && tv <= TouchValue.D8);
    
    private static int GetBitIndex(TouchValue tv) => BitOperations.TrailingZeroCount((ulong)tv);
    
    public void SetRadii(double touchRadius, double buttonRadius)
    {
        _touchRadius = Math.Max(0, touchRadius);
        _buttonRadius = Math.Max(0, buttonRadius);
    }
    
    public void PointerDown(uint id, Point canvasPoint)
    {
        ulong mask = GetMaskAtPoint(canvasPoint);
        var state = new PointerState { LastPoint = canvasPoint, CurrentMask = mask };
        _pointers[id] = state;
        
        ApplyMask(mask, true);
        Trace?.Invoke("POINTER", $"DOWN id={id} at=({canvasPoint.X:F1},{canvasPoint.Y:F1}) mask=0x{mask:X}");
    }
    
    public void PointerMove(uint id, Point canvasPoint)
    {
        if (!_pointers.TryGetValue(id, out var state))
        {
            PointerDown(id, canvasPoint);
            return;
        }
        
        ulong newMask = GetMaskAlongPath(state.LastPoint, canvasPoint);
        ulong added = newMask & ~state.CurrentMask;
        ulong removed = state.CurrentMask & ~newMask;
        
        if (added != 0 || removed != 0)
        {
            Trace?.Invoke("MASK", $"id={id} MOVE at=({canvasPoint.X:F1},{canvasPoint.Y:F1}) prev=0x{state.CurrentMask:X} next=0x{newMask:X} added=0x{added:X} removed=0x{removed:X}");
        }
        
        ApplyMask(added, true);
        ApplyMask(removed, false);
        
        state.LastPoint = canvasPoint;
        state.CurrentMask = newMask;
    }
    
    public void PointerUp(uint id, Point canvasPoint)
    {
        if (_pointers.TryRemove(id, out var state))
        {
            Trace?.Invoke("MASK", $"id={id} UP at=({canvasPoint.X:F1},{canvasPoint.Y:F1}) releasingMask=0x{state.CurrentMask:X}");
            ApplyMask(state.CurrentMask, false);
        }
    }
    
    private ulong GetMaskAtPoint(Point p)
    {
        // Direct polygon hit-testing - no circular sampling, no gaps
        return PointToMask(p);
    }
    
    private ulong GetMaskAlongPath(Point from, Point to)
    {
        var dx = to.X - from.X;
        var dy = to.Y - from.Y;
        var dist = Math.Sqrt(dx * dx + dy * dy);
        var steps = Math.Max(1, (int)(dist / 3));
        ulong result = 0;
        for (int i = 0; i <= steps; i++)
        {
            double t = steps == 0 ? 1.0 : (double)i / steps;
            var p = new Point(from.X + dx * t, from.Y + dy * t);
            result |= GetMaskAtPoint(p);
        }
        return result;
    }
    
    private ulong PointToMask(Point p)
    {
        ulong mask = 0;
        for (int i = 0; i < _sensors.Length; i++)
        {
            var s = _sensors[i];
            if (p.X < s.MinX || p.X > s.MaxX || p.Y < s.MinY || p.Y > s.MaxY)
                continue;
            if (PointInPolygon(p, s.Points))
                mask |= (1UL << i);
        }
        return mask;
    }
    
    private void ApplyMask(ulong mask, bool press)
    {
        while (mask != 0)
        {
            int bit = BitOperations.TrailingZeroCount(mask);
            var tv = _sensors[bit].Value;
            
            if (press)
            {
                if (_holdCounts[bit] == 0)
                {
                    OnPress?.Invoke(tv);
                    Trace?.Invoke("SENSOR", $"PRESS {tv}");
                }
                _holdCounts[bit]++;
            }
            else
            {
                if (_holdCounts[bit] > 0)
                {
                    _holdCounts[bit]--;
                    if (_holdCounts[bit] == 0)
                    {
                        OnRelease?.Invoke(_sensors[bit].Value);
                        Trace?.Invoke("SENSOR", $"RELEASE {tv}");
                    }
                }
            }
            mask &= mask - 1;
        }
    }
    
    private static bool PointInPolygon(Point p, Point[] pts)
    {
        int count = pts.Length;
        if (count < 3) return false;
        bool inside = false;
        double x = p.X, y = p.Y;
        double x0 = pts[count - 1].X, y0 = pts[count - 1].Y;
        for (int i = 0; i < count; i++)
        {
            double x1 = pts[i].X, y1 = pts[i].Y;
            if ((y1 > y) != (y0 > y))
            {
                double xInt = x1 + (y - y1) * (x0 - x1) / (y0 - y1);
                if (xInt > x) inside = !inside;
            }
            x0 = x1; y0 = y1;
        }
        return inside;
    }
    
    public struct SensorGeometry
        {
            public TouchValue Value;
            public Point[] Points;
            public double MinX, MaxX, MinY, MaxY;
            public bool IsButton;
        }
    
    private sealed class PointerState
    {
        public Point LastPoint;
        public ulong CurrentMask;
    }
}

public static class TouchSensorFactory
{
    public static TouchSensorEngine CreateFromTouchPanel(FrameworkElement panel)
    {
        var polygons = new Dictionary<TouchValue, Polygon>();
        
        // Use VisualTreeHelper to find all Polygons in the visual tree
        var allPolygons = VisualTreeHelperExtensions.FindVisualChildren<Polygon>(panel);
        foreach (var p in allPolygons)
        {
            if (p.Tag is TouchValue tv && !polygons.ContainsKey(tv))
                polygons[tv] = p;
        }
        
        return new TouchSensorEngine(polygons);
    }
}