/*
This file is part of MatterSlice. A commandline utility for 
generating 3D printing GCode.

Copyright (C) 2013 David Braam
Copyright (c) 2014, Lars Brubaker

MatterSlice is free software: you can redistribute it and/or modify
it under the terms of the GNU Affero General Public License as
published by the Free Software Foundation, either version 3 of the
License, or (at your option) any later version.

This program is distributed in the hope that it will be useful,
but WITHOUT ANY WARRANTY; without even the implied warranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
GNU Affero General Public License for more details.

You should have received a copy of the GNU Affero General Public License
along with this program.  If not, see <http://www.gnu.org/licenses/>.
*/

using System;
using System.Collections.Generic;
using System.Diagnostics;

using MatterSlice.ClipperLib;

namespace MatterHackers.MatterSlice
{
    using Polygon = List<IntPoint>;
    using Polygons = List<List<IntPoint>>;

    /*
    SliceData
    + Layers[]
      + LayerParts[]
        + OutlinePolygons[]
        + Insets[]
          + Polygons[]
        + SkinPolygons[]
    */

    public class SliceLayerPart
    {
        public AABB boundaryBox = new AABB();
        public Polygons outline = new Polygons();
        public Polygons combBoundery = new Polygons();
        public List<Polygons> insets = new List<Polygons>();
        public Polygons skinOutline = new Polygons();
        public Polygons sparseOutline = new Polygons();
        public int bridgeAngle;
    };

    public class SliceLayer
    {
        public long printZ;
        public List<SliceLayerPart> parts = new List<SliceLayerPart>();
    };

    /******************/
    public class SupportPoint
    {
        public int z;
        public double angleFromHorizon;

        public SupportPoint(int z, double angleFromHorizon)
        {
            this.z = z;
            this.angleFromHorizon = angleFromHorizon;
        }
    }

    public class SupportStorage
    {
        public bool generated;
        public int endAngleDegrees;
        public bool generateInternalSupport;
        public int supportXYDistance_um;
        public int supportLayerHeight_um;
        public int supportZGapLayers;
        public int supportInterfaceLayers;

        public IntPoint gridOffset;
        public int gridScale;
        public int gridWidth, gridHeight;
        public List<List<SupportPoint>> xYGridOfSupportPoints = new List<List<SupportPoint>>();

        static void swap(ref int p0, ref int p1)
        {
            int tmp = p0;
            p0 = p1;
            p1 = tmp;
        }

        static void swap(ref long p0, ref long p1)
        {
            long tmp = p0;
            p0 = p1;
            p1 = tmp;
        }

        static void swap(ref Point3 p0, ref Point3 p1)
        {
            Point3 tmp = p0;
            p0 = p1;
            p1 = tmp;
        }

        private static int SortSupportsOnZ(SupportPoint one, SupportPoint two)
        {
            return one.z.CompareTo(two.z);
        }

        public void GenerateSupportGrid(OptimizedModel model, ConfigSettings config)
        {
            this.generated = false;
            if (config.supportEndAngle < 0)
            {
                return;
            }

            this.generated = true;

            this.gridOffset.X = model.minXYZ_um.x;
            this.gridOffset.Y = model.minXYZ_um.y;
            this.gridScale = 200;
            this.gridWidth = (model.size_um.x / this.gridScale) + 1;
            this.gridHeight = (model.size_um.y / this.gridScale) + 1;
            int gridSize = this.gridWidth * this.gridHeight;
            this.xYGridOfSupportPoints = new List<List<SupportPoint>>(gridSize);
            for (int i = 0; i < gridSize; i++)
            {
                this.xYGridOfSupportPoints.Add(new List<SupportPoint>());
            }

            this.endAngleDegrees = config.supportEndAngle;
            this.generateInternalSupport = config.generateInternalSupport;
            this.supportXYDistance_um = config.supportXYDistance_um;
            this.supportLayerHeight_um = config.layerThickness_um;
            this.supportZGapLayers = config.supportNumberOfLayersToSkipInZ;
            this.supportInterfaceLayers = config.supportInterfaceLayers;

            // This should really be a ray intersection as later code is going to count on it being an even odd list of bottoms and tops.
            // As it is we are finding the hit on the plane but not checking for good intersection with the triangle.
            for (int volumeIndex = 0; volumeIndex < model.volumes.Count; volumeIndex++)
            {
                OptimizedVolume vol = model.volumes[volumeIndex];
                for (int faceIndex = 0; faceIndex < vol.facesTriangle.Count; faceIndex++)
                {
                    OptimizedFace faceTriangle = vol.facesTriangle[faceIndex];
                    Point3 v0Orig = vol.vertices[faceTriangle.vertexIndex[0]].position;
                    Point3 v1Orig = vol.vertices[faceTriangle.vertexIndex[1]].position;
                    Point3 v2Orig = vol.vertices[faceTriangle.vertexIndex[2]].position;

                    // get the angle of this polygon
                    double angleFromHorizon;
                    FPoint3 v0f = new FPoint3(v0Orig);
                    FPoint3 v1f = new FPoint3(v1Orig);
                    FPoint3 v2f = new FPoint3(v2Orig);
                    FPoint3 normal = (v1f - v0f).Cross(v2f - v0f);
                    normal /= normal.Length;

                    angleFromHorizon = (Math.PI / 2) - FPoint3.CalculateAngle(normal, FPoint3.Up);

                    if (angleFromHorizon < Math.PI / 2)
                    {
                        double distanceToPlaneFromOrigin = FPoint3.Dot(normal, v0f);

                        Point3 v0 = v0Orig;
                        Point3 v1 = v1Orig;
                        Point3 v2 = v2Orig;

                        v0.x = (int)((v0.x - this.gridOffset.X) / (double)this.gridScale + .5);
                        v0.y = (int)((v0.y - this.gridOffset.Y) / (double)this.gridScale + .5);
                        v1.x = (int)((v1.x - this.gridOffset.X) / (double)this.gridScale + .5);
                        v1.y = (int)((v1.y - this.gridOffset.Y) / (double)this.gridScale + .5);
                        v2.x = (int)((v2.x - this.gridOffset.X) / (double)this.gridScale + .5);
                        v2.y = (int)((v2.y - this.gridOffset.Y) / (double)this.gridScale + .5);

                        long minX = Math.Min(v0.x, Math.Min(v1.x, v2.x));
                        long maxX = Math.Max(v0.x, Math.Max(v1.x, v2.x));
                        long minY = Math.Min(v0.y, Math.Min(v1.y, v2.y));
                        long maxY = Math.Max(v0.y, Math.Max(v1.y, v2.y));

                        for (long x = minX; x < maxX; x++)
                        {
                            for (long y = minY; y < maxY; y++)
                            {
                                Point3 ray = new Point3(x * gridScale + gridOffset.X, y * gridScale + gridOffset.Y, 0);
                                if (Have2DHitOnTriangle(v0Orig, v1Orig, v2Orig, ray.x, ray.y))
                                {
                                    double z = DistanceToPlane(normal, new FPoint3(ray.x, ray.y, ray.z), distanceToPlaneFromOrigin);
                                    SupportPoint newSupportPoint = new SupportPoint((int)z, angleFromHorizon);
                                    this.xYGridOfSupportPoints[(int)(x + y * this.gridWidth)].Add(newSupportPoint);
                                }
                            }
                        }
                    }
                }
            }

            // now remove duplicates (try to make it a better bottom and top list)
            for (int x = 0; x < this.gridWidth; x++)
            {
                for (int y = 0; y < this.gridHeight; y++)
                {
                    int gridIndex = x + y * this.gridWidth;
                    List<SupportPoint> currentList = this.xYGridOfSupportPoints[gridIndex];
                    currentList.Sort(SortSupportsOnZ);

                    if(currentList.Count > 1)
                    {
                        for (int i = currentList.Count-1; i>=1; i--)
                        {
                            if (currentList[i].z == currentList[i - 1].z)
                            {
                                currentList.RemoveAt(i);
                            }
                        }
                    }
                }
            }
            this.gridOffset.X += this.gridScale / 2;
            this.gridOffset.Y += this.gridScale / 2;
        }

        static readonly double TreatAsZero = 0.001;
        public double DistanceToPlane(FPoint3 planeNormal, FPoint3 upRay, double distanceToPlaneFromOrigin)
        {

            double normalDotRayDirection = FPoint3.Dot(planeNormal, FPoint3.Up);
            if (normalDotRayDirection < TreatAsZero && normalDotRayDirection > -TreatAsZero) // the ray is parallel to the plane
            {
                return 0;
            }

            double distanceToRayOriginFromOrigin = FPoint3.Dot(planeNormal, upRay);

            double distanceToPlaneFromRayOrigin = distanceToPlaneFromOrigin - distanceToRayOriginFromOrigin;

            bool originInFrontOfPlane = distanceToPlaneFromRayOrigin < 0;

            double distanceToHit = distanceToPlaneFromRayOrigin / normalDotRayDirection;
            return distanceToHit;
        }

        public int FindSideOfLine(Point3 sidePoint0, Point3 sidePoint1, double x, double y)
        {
            double leftX = x - sidePoint0.x;
            double leftY = y - sidePoint0.y;
            double rightX = x - sidePoint1.x;
            double rightY = y - sidePoint1.y;
            if (leftX * rightY - leftY * rightX < 0)
            {
                return 1;
            }

            return -1;
        }

        bool Have2DHitOnTriangle(Point3 v0, Point3 v1, Point3 v2, double x, double y)
        {
            // check the bounding rect
            int sumOfLineSides = FindSideOfLine(v0, v1, x, y);
            sumOfLineSides += FindSideOfLine(v1, v2, x, y);
            sumOfLineSides += FindSideOfLine(v2, v0, x, y);
            if (sumOfLineSides == -3 || sumOfLineSides == 3)
            {
                return true;
            }

            return false;
        }
    }

    /******************/

    public class SliceVolumeStorage
    {
        public List<SliceLayer> layers = new List<SliceLayer>();
    }

    public class SliceDataStorage
    {
        public Point3 modelSize, modelMin, modelMax;
        public Polygons skirt = new Polygons();
        public Polygons raftOutline = new Polygons();
        public List<Polygons> wipeShield = new List<Polygons>();
        public List<SliceVolumeStorage> volumes = new List<SliceVolumeStorage>();

        public SupportStorage support = new SupportStorage();
        public Polygons wipeTower = new Polygons();
        public IntPoint wipePoint;
    }
}
