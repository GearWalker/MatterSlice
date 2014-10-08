/*
Copyright (C) 2013 David Braam
Copyright (c) 2014, Lars Brubaker

This file is part of MatterSlice.

MatterSlice is free software: you can redistribute it and/or modify
it under the terms of the GNU Lesser General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.

MatterSlice is distributed in the hope that it will be useful,
but WITHOUT ANY WARRANTY; without even the implied warranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
GNU General Public License for more details.

You should have received a copy of the GNU General Public License
along with MatterSlice.  If not, see <http://www.gnu.org/licenses/>.
*/

using System;
using System.Collections.Generic;
using System.Diagnostics;

using MatterSlice.ClipperLib;
using MatterHackers.PolygonMesh;
using MatterHackers.VectorMath;

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
        public double cosAngle;

        public SupportPoint(int z, double cosAngle)
        {
            this.z = z;
            this.cosAngle = cosAngle;
        }
    }

    public class SupportStorage
    {
        public bool generated;
        public int endAngle;
        public bool generateInternalSupport;
        public int supportXYDistance_um;
        public int supportZDistance_um;
        public int interfaceLayers;

        public IntPoint gridOffset_um;
        public int gridScale;
        public int gridWidth_um, gridHeight_um;
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

        public void GenerateSupportGrid(MeshGroup meshGroup, ConfigSettings config)
        {
            this.generated = false;
            if (config.supportEndAngle < 0)
            {
                return;
            }

            this.generated = true;

            AxisAlignedBoundingBox bounds = meshGroup.GetAxisAlignedBoundingBox();
            this.gridOffset_um.X = (int)(bounds.minXYZ.x * 1000 + .5);
            this.gridOffset_um.Y = (int)(bounds.minXYZ.y * 1000 + .5);
            this.gridScale = 200;
            this.gridWidth_um = (((int)(bounds.XSize * 1000 + .5)) / this.gridScale) + 1;
            this.gridHeight_um = (((int)(bounds.YSize * 1000 + .5)) / this.gridScale) + 1;
            int gridSize = this.gridWidth_um * this.gridHeight_um;
            this.xYGridOfSupportPoints = new List<List<SupportPoint>>(gridSize);
            for (int i = 0; i < gridSize; i++)
            {
                this.xYGridOfSupportPoints.Add(new List<SupportPoint>());
            }

            this.endAngle = config.supportEndAngle;
            this.generateInternalSupport = config.generateInternalSupport;
            this.supportXYDistance_um = config.supportXYDistance_um;
            this.supportZDistance_um = config.supportNumberOfLayersToSkipInZ * config.layerThickness_um;
            this.interfaceLayers = config.supportInterfaceLayers;

            // This should really be a ray intersection as later code is going to count on it being an even odd list of bottoms and tops.
            // As it is we are finding the hit on the plane but not checking for good intersection with the triangle.
            for (int meshIndex = 0; meshIndex < meshGroup.Meshes.Count; meshIndex++)
            {
                Mesh mesh = meshGroup.Meshes[meshIndex];
                for (int faceIndex = 0; faceIndex < mesh.Faces.Count; faceIndex++)
                {
                    Face faceTriangle = mesh.Faces[faceIndex];
                    int vertexIndex = 0;
                    Point3 v0 = new Point3();
                    Point3 v1 = new Point3();
                    Point3 v2 = new Point3();
                    foreach (Vertex vertex in faceTriangle.Vertices())
                    {
                        switch (vertexIndex)
                        {
                            case 0:
                                v0 = new Point3(vertex.Position.x * 1000, vertex.Position.y * 1000, vertex.Position.z * 1000);
                                break;
                            case 1:
                                v1 = new Point3(vertex.Position.x * 1000, vertex.Position.y * 1000, vertex.Position.z * 1000);
                                break;
                            case 2:
                                v2 = new Point3(vertex.Position.x * 1000, vertex.Position.y * 1000, vertex.Position.z * 1000);
                                break;
                        }
                    }

                    Point3 normal = (v1 - v0).cross(v2 - v0);
                    int normalSize = normal.vSize();

                    double cosAngle = Math.Abs((double)(normal.z) / (double)(normalSize));

                    v0.x = (int)((v0.x - this.gridOffset_um.X) / (double)this.gridScale + .5);
                    v0.y = (int)((v0.y - this.gridOffset_um.Y) / (double)this.gridScale + .5);
                    v1.x = (int)((v1.x - this.gridOffset_um.X) / (double)this.gridScale + .5);
                    v1.y = (int)((v1.y - this.gridOffset_um.Y) / (double)this.gridScale + .5);
                    v2.x = (int)((v2.x - this.gridOffset_um.X) / (double)this.gridScale + .5);
                    v2.y = (int)((v2.y - this.gridOffset_um.Y) / (double)this.gridScale + .5);

                    if (v0.x > v1.x) swap(ref v0, ref v1);
                    if (v1.x > v2.x) swap(ref v1, ref v2);
                    if (v0.x > v1.x) swap(ref v0, ref v1);
                    for (long x = v0.x; x < v1.x; x++)
                    {
                        long y0 = (long)(v0.y + (v1.y - v0.y) * (x - v0.x) / (double)(v1.x - v0.x) + .5);
                        long y1 = (long)(v0.y + (v2.y - v0.y) * (x - v0.x) / (double)(v2.x - v0.x) + .5);
                        long z0 = (long)(v0.z + (v1.z - v0.z) * (x - v0.x) / (double)(v1.x - v0.x) + .5);
                        long z1 = (long)(v0.z + (v2.z - v0.z) * (x - v0.x) / (double)(v2.x - v0.x) + .5);

                        if (y0 > y1)
                        {
                            swap(ref y0, ref y1);
                            swap(ref z0, ref z1);
                        }

                        for (long y = y0; y < y1; y++)
                        {
                            SupportPoint newSupportPoint = new SupportPoint((int)(z0 + (z1 - z0) * (y - y0) / (double)(y1 - y0) + .5), cosAngle);
                            this.xYGridOfSupportPoints[(int)(x + y * this.gridWidth_um)].Add(newSupportPoint);
                        }
                    }

                    for (int x = v1.x; x < v2.x; x++)
                    {
                        long y0 = (long)(v1.y + (v2.y - v1.y) * (x - v1.x) / (double)(v2.x - v1.x) + .5);
                        long y1 = (long)(v0.y + (v2.y - v0.y) * (x - v0.x) / (double)(v2.x - v0.x) + .5);
                        long z0 = (long)(v1.z + (v2.z - v1.z) * (x - v1.x) / (double)(v2.x - v1.x) + .5);
                        long z1 = (long)(v0.z + (v2.z - v0.z) * (x - v0.x) / (double)(v2.x - v0.x) + .5);

                        if (y0 > y1)
                        {
                            swap(ref y0, ref y1);
                            swap(ref z0, ref z1);
                        }

                        for (int y = (int)y0; y < y1; y++)
                        {
                            this.xYGridOfSupportPoints[x + y * this.gridWidth_um].Add(new SupportPoint((int)(z0 + (z1 - z0) * (double)(y - y0) / (double)(y1 - y0) + .5), cosAngle));
                        }
                    }
                }
            }

            for (int x = 0; x < this.gridWidth_um; x++)
            {
                for (int y = 0; y < this.gridHeight_um; y++)
                {
                    int gridIndex = x + y * this.gridWidth_um;
                    List<SupportPoint> currentList = this.xYGridOfSupportPoints[gridIndex];
                    currentList.Sort(SortSupportsOnZ);

                    if(currentList.Count > 1)
                    {
                        // now remove duplicates (try to make it a better bottom and top list)
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
            this.gridOffset_um.X += this.gridScale / 2;
            this.gridOffset_um.Y += this.gridScale / 2;
        }
    }

    /******************/

    public class SliceVolumeStorage
    {
        public List<SliceLayer> layers = new List<SliceLayer>();
    }

    public class SliceDataStorage
    {
        public Point3 modelSize_um, modelMin_um, modelMax_um;
        public Polygons skirt = new Polygons();
        public Polygons raftOutline = new Polygons();
        public List<Polygons> wipeShield = new List<Polygons>();
        public List<SliceVolumeStorage> volumes = new List<SliceVolumeStorage>();

        public SupportStorage support = new SupportStorage();
        public Polygons wipeTower = new Polygons();
        public IntPoint wipePoint;
    }
}
