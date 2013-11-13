﻿#region License
/**
 * Copyright (c) 2013 Robert Rouhani <robert.rouhani@gmail.com> and other contributors (see CONTRIBUTORS file).
 * Licensed under the MIT License - https://raw.github.com/Robmaister/SharpNav/master/LICENSE
 */
#endregion

using System;
using System.Collections.Generic;
using SharpNav.Geometry;

namespace SharpNav
{
	public class ContourSet
	{
		//flags used in the build process
		public const int BORDER_VERTEX = 0x10000;
		public const int AREA_BORDER = 0x20000;
		public const int CONTOUR_TESS_WALL_EDGES = 0x01;
		public const int CONTOUR_TESS_AREA_EDGES = 0x02;

		//applied to region id field of contour vertices in order to extract region id
		public const int CONTOUR_REG_MASK = 0xffff;

		private List<Contour> contours;
		
		private BBox3 bounds;
		private float cellSize;
		private float cellHeight;
		private int width;
		private int height;
		private int borderSize;

		/// <summary>
		/// Create contours by tracing edges around the regions generated by the open heightfield.
		/// </summary>
		/// <param name="openField">The OpenHeightfield provides the region data.</param>
		/// <param name="maxError">Amound of error allowed in simplification</param>
		/// <param name="maxEdgeLen">Limit the length of an edge.</param>
		/// <param name="buildFlags">?</param>
		public ContourSet(CompactHeightfield openField, float maxError, int maxEdgeLen, int buildFlags)
		{
			//copy the OpenHeightfield data into ContourSet
			this.bounds = openField.Bounds;
			if (openField.BorderSize > 0)
			{
				//remove offset
				float pad = openField.BorderSize * openField.CellSize;
				this.bounds.Min.X += pad;
				this.bounds.Min.Z += pad;
				this.bounds.Max.X -= pad;
				this.bounds.Max.Z -= pad;
			}
			this.cellSize = openField.CellSize;
			this.cellHeight = openField.CellHeight;
			this.width = openField.Width - openField.BorderSize * 2;
			this.height = openField.Height - openField.BorderSize * 2;
			this.borderSize = openField.BorderSize;

			int maxContours = Math.Max((int)openField.MaxRegions, 8);
			contours = new List<Contour>(maxContours);

			byte[] flags = new byte[openField.Spans.Length];

			//Modify flags array by using the OpenHeightfield data
			//mark boundaries
			for (int y = 0; y < openField.Length; y++)
			{
				for (int x = 0; x < openField.Width; x++)
				{
					//loop through all the spans in the cell
					CompactCell c = openField.Cells[x + y * openField.Width];
					for (int i = c.StartIndex, end = c.StartIndex + c.Count; i < end; i++)
					{
						byte res = 0;
						CompactSpan s = openField.Spans[i];

						//if the region doesn't have an id (so default is 0?) or the region id satisfies border flag (so border flag returns 1?)
						//then set the flag to 0
						if (openField.Spans[i].Region == 0 || (openField.Spans[i].Region & CompactHeightfield.BORDER_REG) != 0)
						{
							flags[i] = 0;
							continue;
						}

						//go through all the neighboring cells
						for (int dir = 0; dir < 4; dir++)
						{
							//obtain region id
							int r = 0;
							if (CompactSpan.GetConnection(dir, ref s) != CompactSpan.NotConnected)
							{
								int dx = x + MathHelper.GetDirOffsetX(dir);
								int dy = y + MathHelper.GetDirOffsetY(dir);
								int di = openField.Cells[dx + dy * openField.Width].StartIndex + CompactSpan.GetConnection(dir, ref s);
								r = openField.Spans[di].Region;
							}

							//region ids are equal
							if (r == openField.Spans[i].Region)
							{
								//res marks all the INTERNAL edges
								//res represented as 4 bits (left bit represents dir = 3, right bit represents dir = 0)
								//default is 0000
								//the |= operation sets each direction bit to 1 (so if dir = 0, 0000 -> 0001)

								res |= (byte)(1 << dir);
							}
						}

						//flips all the bits in res
						//0000 (completely internal) -> 1111
						//1111 (no internal edges) -> 0000

						//flags represents all the nonconnected edges, edges that are only internal
						//the edges need to be between different regions
						flags[i] = (byte)(res ^ 0xf); 
					}
				}
			}

			//Points are in format (each point takes up 4 array elements): 
			//	(x, y, z) coordinates
			//	region id
			List<int> verts = new List<int>();
			List<int> simplified = new List<int>();

			int numContours = 0;
			for (int y = 0; y < openField.Length; y++)
			{
				for (int x = 0; x < openField.Width; x++)
				{
					CompactCell c = openField.Cells[x + y * openField.Width];
					for (int i = c.StartIndex, end = c.StartIndex + c.Count; i < end; i++)
					{
						//flags is either 0000 or 1111
						//in other words, not connected at all 
						//or has all connections, which means span is in the middle and thus not an edge.
						if (flags[i] == 0 || flags[i] == 0xf)
						{
							flags[i] = 0;
							continue;
						}

						int reg = openField.Spans[i].Region;
						if (reg == 0 || (reg & CompactHeightfield.BORDER_REG) != 0)
							continue;
						
						AreaFlags area = openField.Areas[i];
						
						//reset each iteration
						verts.Clear();
						simplified.Clear();

						//Mark points, which are basis of contous, intially with "verts"
						//Then, simplify "verts" to get "simplified"
						//Finally, clean up the "simplified" data
						WalkContour(x, y, i, openField, flags, verts);
						SimplifyContour(verts, simplified, maxError, maxEdgeLen, buildFlags);
						RemoveDegenerateSegments(simplified);

						if (simplified.Count / 4 >= 3)
						{
							Contour cont = new Contour();
							
							//Save all the simplified and raw data in the Contour
							cont.NumVerts = simplified.Count / 4;
							cont.Vertices = new int[cont.NumVerts * 4];
							for (int j = 0; j < cont.Vertices.Length; j++)
								cont.Vertices[j] = simplified[j];

							if (borderSize > 0)
							{
								//remove offset
								for (int j = 0; j < cont.NumVerts; j++)
								{
									cont.Vertices[j * 4 + 0] -= borderSize;
									cont.Vertices[j * 4 + 2] -= borderSize;
								}
							}

							//copy raw data
							cont.NumRawVerts = verts.Count / 4;
							cont.RawVertices = new int[cont.NumRawVerts * 4];
							for (int j = 0; j < cont.RawVertices.Length; j++)
								cont.RawVertices[j] = verts[j];

							if (borderSize > 0)
							{
								//remove offset
								for (int j = 0; j < cont.NumRawVerts; j++)
								{
									cont.RawVertices[j * 4 + 0] -= borderSize;
									cont.RawVertices[j * 4 + 2] -= borderSize;
								}
							}

							cont.RegionId = reg;
							cont.Area = area;

							contours.Add(cont);
							numContours++;
						}
					}
				}
			}

			//Check and merge holes
			for (int i = 0; i < numContours; i++)
			{
				Contour cont = contours[i];

				//check if contour is backwards
				if (CalcAreaOfPolygon2D(cont.Vertices, cont.NumVerts) < 0)
				{
					//find another contour with same region id
					int mergeIdx = -1;
					for (int j = 0; j < numContours; j++)
					{
						//don't compare to itself
						if (i == j) continue;

						//same region id
						if (contours[j].NumVerts != 0 && contours[j].RegionId == cont.RegionId)
						{
							//make sure polygon is correctly oriented
							if (CalcAreaOfPolygon2D(contours[j].Vertices, cont.NumVerts) > 0)
							{
								mergeIdx = j;
								break;
							}
						}
					}

					//only merge if needed
					if (mergeIdx != -1)
					{
						Contour mcont = contours[mergeIdx];

						//merge by closest points
						int ia = 0, ib = 0;
						GetClosestIndices(mcont.Vertices, mcont.NumVerts, cont.Vertices, cont.NumVerts, ref ia, ref ib);
						if (ia == -1 || ib == -1)
							continue;

						MergeContours(ref mcont, cont, ia, ib);
					}
				}
			}
		}

		public List<Contour> Contours
		{
			get
			{
				return contours;
			}
		}

		public BBox3 Bounds
		{
			get
			{
				return bounds;
			}
		}

		public float CellSize
		{
			get
			{
				return cellSize;
			}
		}

		public float CellHeight
		{
			get
			{
				return cellHeight;
			}
		}

		public int Width
		{
			get
			{
				return width;
			}
		}

		public int Height
		{
			get
			{
				return height;
			}
		}

		public int BorderSize
		{
			get
			{
				return borderSize;
			}
		}

		/// <summary>
		/// Initial generation of the contours
		/// </summary>
		/// <param name="x">Cell x</param>
		/// <param name="y">Cell y</param>
		/// <param name="i">Span index</param>
		/// <param name="openField">OpenHeightfield</param>
		/// <param name="flags">?</param>
		/// <param name="points">Vertices of contour</param>
		public void WalkContour(int x, int y, int i, CompactHeightfield openField, byte[] flags, List<int> points)
		{
			int dir = 0;

			//four bits, each bit represents a direction (0 = non-connected, 1 = connected)
			//find the first rightmost bit that equals 0 (non-connected)
			//ex:	0001 & 0001 = 1
			//		0001 & 0010 = 0 (found!)
			while ((flags[i] & (1 << dir)) == 0)
				dir++;

			int startDir = dir;
			int starti = i;

			AreaFlags area = openField.Areas[i];

			int iter = 0;
			while (++iter < 40000)
			{
				//this direction is connected
				if ((flags[i] & (1 << dir)) != 0)
				{
					//choose the edge corner
					bool isBorderVertex = false;
					bool isAreaBorder = false;

					int px = x;
					int py = GetCornerHeight(x, y, i, dir, openField, ref isBorderVertex);
					int pz = y;

					switch (dir)
					{
						case 0:
							pz++;
							break;
						
						case 1:
							px++;
							pz++;
							break;
						
						case 2:
							px++;
							break;
					}

					int r = 0;
					CompactSpan s = openField.Spans[i];
					if (CompactSpan.GetConnection(dir, ref s) != CompactSpan.NotConnected)
					{
						int dx = x + MathHelper.GetDirOffsetX(dir);
						int dy = y + MathHelper.GetDirOffsetY(dir);
						int di = openField.Cells[dx + dy * openField.Width].StartIndex + CompactSpan.GetConnection(dir, ref s);
						r = openField.Spans[di].Region;
						if (area != openField.Areas[di])
							isAreaBorder = true;
					}
					
					//apply flags if neccessary
					if (isBorderVertex)
						r |= BORDER_VERTEX;

					if (isAreaBorder)
						r |= AREA_BORDER;
					
					//save the point (x,y,z) coordinate and region id
					points.Add(px);
					points.Add(py);
					points.Add(pz);
					points.Add(r);

					//say flag = 0110
					//dir = 2 (so 1 << dir = 0100)
					//~dir = 1011
					//flag &= ~dir
					//flag = 0110 & 1011 = 0010
					flags[i] &= (byte)(~(1 << dir)); // remove visited edges
					dir = (dir + 1) % 4; //rotate clockwise
				}
				else
				{
					//get a new cell(x, y) and span index(i)
					int di = -1;
					int dx = x + MathHelper.GetDirOffsetX(dir);
					int dy = y + MathHelper.GetDirOffsetY(dir);
					
					CompactSpan s = openField.Spans[i];
					if (CompactSpan.GetConnection(dir, ref s) != CompactSpan.NotConnected)
					{
						CompactCell dc = openField.Cells[dx + dy * openField.Width];
						di = dc.StartIndex + CompactSpan.GetConnection(dir, ref s);
					}
					
					if (di == -1)
					{
						//shouldn't happen
						return;
					}
					
					x = dx;
					y = dy;
					i = di;
					dir = (dir + 3) % 4; //rotate counterclockwise
				}

				if (starti == i && startDir == dir)
				{
					break;
				}
			}
		}

		/// <summary>
		/// Helper method for WalkContour function
		/// </summary>
		/// <param name="x">Cell x</param>
		/// <param name="y">Cell y</param>
		/// <param name="i">Span index i</param>
		/// <param name="dir">Direction (west, north, east, south)</param>
		/// <param name="openField">OpenHeightfield</param>
		/// <param name="isBorderVertex">Determine whether the vertex is a border or not</param>
		/// <returns></returns>
		public int GetCornerHeight(int x, int y, int i, int dir, CompactHeightfield openField, ref bool isBorderVertex)
		{
			CompactSpan s = openField.Spans[i];
			int cornerHeight = s.Minimum;
			int dirp = (dir + 1) % 4; //new clockwise direction

			uint[] regs = { 0, 0, 0, 0 };

			//combine region and area codes in order to prevent border vertices, which are in between two areas, to be removed 
			regs[0] = (uint)(openField.Spans[i].Region | ((byte)(openField.Areas[i]) << 16));

			if (CompactSpan.GetConnection(dir, ref s) != CompactSpan.NotConnected)
			{
				//get neighbor span
				int dx = x + MathHelper.GetDirOffsetX(dir);
				int dy = y + MathHelper.GetDirOffsetY(dir);
				int di = openField.Cells[dx + dy * openField.Width].StartIndex + CompactSpan.GetConnection(dir, ref s);
				CompactSpan ds = openField.Spans[di];

				cornerHeight = Math.Max(cornerHeight, ds.Minimum);
				regs[1] = (uint)(openField.Spans[di].Region | ((byte)(openField.Areas[di]) << 16));

				//get neighbor of neighbor's span
				if (CompactSpan.GetConnection(dirp, ref ds) != CompactSpan.NotConnected)
				{
					int dx2 = dx + MathHelper.GetDirOffsetX(dirp);
					int dy2 = dy + MathHelper.GetDirOffsetY(dirp);
					int di2 = openField.Cells[dx2 + dy2 * openField.Width].StartIndex + CompactSpan.GetConnection(dirp, ref ds);
					CompactSpan ds2 = openField.Spans[di2];

					cornerHeight = Math.Max(cornerHeight, ds2.Minimum);
					regs[2] = (uint)(openField.Spans[di2].Region | ((byte)(openField.Areas[di2]) << 16));
				}
			}

			//get neighbor span
			if (CompactSpan.GetConnection(dirp, ref s) != CompactSpan.NotConnected)
			{
				int dx = x + MathHelper.GetDirOffsetX(dirp);
				int dy = y + MathHelper.GetDirOffsetY(dirp);
				int di = openField.Cells[dx + dy * openField.Width].StartIndex + CompactSpan.GetConnection(dirp, ref s);
				CompactSpan ds = openField.Spans[di];

				cornerHeight = Math.Max(cornerHeight, ds.Minimum);
				regs[3] = (uint)(openField.Spans[di].Region | ((byte)(openField.Areas[di]) << 16));

				//get neighbor of neighbor's span
				if (CompactSpan.GetConnection(dir, ref ds) != CompactSpan.NotConnected)
				{
					int dx2 = dx + MathHelper.GetDirOffsetX(dir);
					int dy2 = dy + MathHelper.GetDirOffsetY(dir);
					int di2 = openField.Cells[dx2 + dy2 * openField.Width].StartIndex + CompactSpan.GetConnection(dir, ref ds);
					CompactSpan ds2 = openField.Spans[di2];

					cornerHeight = Math.Max(cornerHeight, ds2.Minimum);
					regs[2] = (uint)(openField.Spans[di2].Region | ((byte)openField.Areas[di2] << 16));
				}
			}

			//check if vertex is special edge vertex
			//if so, these vertices will be removed later
			for (int j = 0; j < 4; j++)
			{
				int a = j;
				int b = (j + 1) % 4;
				int c = (j + 2) % 4;
				int d = (j + 3) % 4;

				//the vertex is a border vertex if:
				//two same exterior cells in a row followed by two interior cells and none of the regions are out of bounds
				bool twoSameExteriors = (regs[a] & regs[b] & CompactHeightfield.BORDER_REG) != 0 && regs[a] == regs[b];
				bool twoSameInteriors = ((regs[c] | regs[d]) & CompactHeightfield.BORDER_REG) == 0;
				bool intsSameArea = (regs[c] >> 16) == (regs[d] >> 16);
				bool noZeros = regs[a] != 0 && regs[b] != 0 && regs[c] != 0 && regs[d] != 0;
				if (twoSameExteriors && twoSameInteriors && intsSameArea && noZeros)
				{
					isBorderVertex = true;
					break;
				}
			}

			return cornerHeight;
		}

		/// <summary>
		/// Simplify the contours by reducing the number of edges
		/// </summary>
		/// <param name="points">Initial vertices</param>
		/// <param name="simplified">New and simplified vertices</param>
		public void SimplifyContour(List<int> points, List<int> simplified, float maxError, int maxEdgeLen, int buildFlags)
		{
			//add initial points
			bool hasConnections = false;
			for (int i = 0; i < points.Count; i += 4)
			{
				if ((points[i + 3] & CONTOUR_REG_MASK) != 0)
				{
					hasConnections = true;
					break;
				}
			}

			if (hasConnections)
			{
				//contour has some portals to other regions
				//add new point to every location where region changes
				for (int i = 0, end = points.Count / 4; i < end; i++)
				{
					int ii = (i + 1) % end;
					bool differentRegions = (points[i * 4 + 3] & CONTOUR_REG_MASK) != (points[ii * 4 + 3] & CONTOUR_REG_MASK);
					bool areaBorders = (points[i * 4 + 3] & AREA_BORDER) != (points[ii * 4 + 3] & AREA_BORDER);
					
					if (differentRegions || areaBorders)
					{
						simplified.Add(points[i * 4 + 0]);
						simplified.Add(points[i * 4 + 1]);
						simplified.Add(points[i * 4 + 2]);
						simplified.Add(i);
					}
				}
			}

			//add some points if thhere are no connections
			if (simplified.Count == 0)
			{
				//find lower-left and upper-right vertices of contour
				int lowerLeftX = points[0];
				int lowerLeftY = points[1];
				int lowerLeftZ = points[2];
				int lowerLeftI = 0;
				
				int upperRightX = points[0];
				int upperRightY = points[1];
				int upperRightZ = points[2];
				int upperRightI = 0;
				
				//iterate through points, make sure to skip 4 elements each time
				for (int i = 0; i < points.Count; i += 4)
				{
					int x = points[i + 0];
					int y = points[i + 1];
					int z = points[i + 2];
					
					if (x < lowerLeftX || (x == lowerLeftX && z < lowerLeftZ))
					{
						lowerLeftX = x;
						lowerLeftY = y;
						lowerLeftZ = z;
						lowerLeftI = i / 4;
					}
					
					if (x > upperRightX || (x == upperRightX && z > upperRightZ))
					{
						upperRightX = x;
						upperRightY = y;
						upperRightZ = z;
						upperRightI = i / 4;
					}
				}
				
				//save the points
				simplified.Add(lowerLeftX);
				simplified.Add(lowerLeftY);
				simplified.Add(lowerLeftZ);
				simplified.Add(lowerLeftI);

				simplified.Add(upperRightX);
				simplified.Add(upperRightY);
				simplified.Add(upperRightZ);
				simplified.Add(upperRightI);
			}

			//add points until all points are within erorr tolerance of simplified slope
			int numPoints = points.Count / 4;
			for (int i = 0; i < simplified.Count / 4; )
			{
				int ii = (i + 1) % (simplified.Count / 4);

				//obtain (x, z) coordinates, along with region id
				int ax = simplified[i * 4 + 0];
				int az = simplified[i * 4 + 2];
				int ai = simplified[i * 4 + 3];

				int bx = simplified[ii * 4 + 0];
				int bz = simplified[ii * 4 + 2];
				int bi = simplified[ii * 4 + 3];

				float maxDeviation = 0;
				int maxi = -1;
				int ci, cinc, endi;

				//traverse segment in lexilogical order (try to go from smallest to largest coordinates?)
				if (bx > ax || (bx == ax && bz > az))
				{
					cinc = 1;
					ci = (ai + cinc) % numPoints;
					endi = bi;
				}
				else
				{
					cinc = numPoints - 1;
					ci = (bi + cinc) % numPoints;
					endi = ai;
				}

				//tessellate only outer edges or edges between areas
				if ((points[ci * 4 + 3] & CONTOUR_REG_MASK) == 0 || (points[ci * 4 + 3] & AREA_BORDER) != 0)
				{
					//find the maximum deviation
					while (ci != endi)
					{
						float deviation = DistancePointSegment(points[ci * 4 + 0], points[ci * 4 + 2], ax, az, bx, bz);
						
						if (deviation > maxDeviation)
						{
							maxDeviation = deviation;
							maxi = ci;
						}
						
						ci = (ci + cinc) % numPoints;
					}
				}

				//If max deviation is larger than accepted error, add new point
				if (maxi != -1 && maxDeviation > (maxError * maxError))
				{
					//add extra space to list
					for (int j = 0; j < 4; j++)
						simplified.Add(new int());

					//make space for new point by shifting elements to the right
					//ex: element at index 5 is now at index 6, since array[6] takes the value of array[6 - 1]
					for (int j = simplified.Count / 4 - 1; j > i; j--)
					{
						simplified[j * 4 + 0] = simplified[(j - 1) * 4 + 0];
						simplified[j * 4 + 1] = simplified[(j - 1) * 4 + 1];
						simplified[j * 4 + 2] = simplified[(j - 1) * 4 + 2];
						simplified[j * 4 + 3] = simplified[(j - 1) * 4 + 3];
					}

					//add point 
					simplified[(i + 1) * 4 + 0] = points[maxi * 4 + 0];
					simplified[(i + 1) * 4 + 1] = points[maxi * 4 + 1];
					simplified[(i + 1) * 4 + 2] = points[maxi * 4 + 2];
					simplified[(i + 1) * 4 + 3] = maxi;
				}
				else
				{
					i++;
				}
			}

			//split too long edges
			if (maxEdgeLen > 0 && (buildFlags & (CONTOUR_TESS_WALL_EDGES | CONTOUR_TESS_AREA_EDGES)) != 0)
			{
				for (int i = 0; i < simplified.Count / 4; )
				{
					int ii = (i + 1) % (simplified.Count / 4);

					//get (x, z) coordinates along with region id
					int ax = simplified[i * 4 + 0];
					int az = simplified[i * 4 + 2];
					int ai = simplified[i * 4 + 3];

					int bx = simplified[ii * 4 + 0];
					int bz = simplified[ii * 4 + 2];
					int bi = simplified[ii * 4 + 3];

					//find maximum deviation from segment
					int maxi = -1;
					int ci = (ai + 1) % numPoints;

					//tessellate only outer edges or edges between areas
					bool tess = false;

					//wall edges
					if ((buildFlags & CONTOUR_TESS_WALL_EDGES) != 0 && (points[ci * 4 + 3] & CONTOUR_REG_MASK) == 0)
						tess = true;

					//edges between areas
					if ((buildFlags & CONTOUR_TESS_AREA_EDGES) != 0 && (points[ci * 4 + 3] & AREA_BORDER) != 0)
						tess = true;

					if (tess)
					{
						int dx = bx - ax;
						int dz = bz - az;
						if (dx * dx + dz * dz > maxEdgeLen * maxEdgeLen)
						{
							//round based on lexilogical direction (smallest to largest cooridinates, first by x.
							//if x coordinates are equal, then compare z coordinates)
							int n = bi < ai ? (bi + numPoints - ai) : (bi - ai);
							
							if (n > 1)
							{
								if (bx > ax || (bx == ax && bz > az))
									maxi = (ai + n / 2) % numPoints;
								else
									maxi = (ai + (n + 1) / 2) % numPoints;
							}
						}
					}

					//add new point
					if (maxi != -1)
					{
						//add extra space to list
						for (int j = 0; j < 4; j++)
							simplified.Add(new int());

						//make space for new point by shifting elements to the right
						//ex: element at index 5 is now at index 6, since array[6] takes the value of array[6 - 1]
						for (int j = simplified.Count / 4 - 1; j > i; j--)
						{
							simplified[j * 4 + 0] = simplified[(j - 1) * 4 + 0];
							simplified[j * 4 + 1] = simplified[(j - 1) * 4 + 1];
							simplified[j * 4 + 2] = simplified[(j - 1) * 4 + 2];
							simplified[j * 4 + 3] = simplified[(j - 1) * 4 + 3];
						}

						//add point
						simplified[(i + 1) * 4 + 0] = points[maxi * 4 + 0];
						simplified[(i + 1) * 4 + 1] = points[maxi * 4 + 1];
						simplified[(i + 1) * 4 + 2] = points[maxi * 4 + 2];
						simplified[(i + 1) * 4 + 3] = maxi;
					}
					else
					{
						i++;
					}
				}
			}

			for (int i = 0; i < simplified.Count / 4; i++)
			{
				//take edge vertex flag from current raw point and neighbor region from next raw point
				int ai = (simplified[i * 4 + 3] + 1) % numPoints;
				int bi = simplified[i * 4 + 3];

				//save new region id
				simplified[i * 4 + 3] = (points[ai * 4 + 3] & (CONTOUR_REG_MASK | AREA_BORDER)) | (points[bi * 4 + 3] & BORDER_VERTEX);
			}
		}

		/// <summary>
		/// Helper function for the SimplifyContour function.
		/// Find the distance between a point (x, z) and segment PQ
		/// Segment made out of two points P(px, pz) and Q(qx, qz).
		/// </summary>
		/// <param name="x">Individual point x</param>
		/// <param name="z">Individual point z</param>
		/// <param name="px">Segment point 1 x</param>
		/// <param name="pz">Segment point 1 z</param>
		/// <param name="qx">Segment point 2 x</param>
		/// <param name="qz">Segment point 2 z</param>
		/// <returns></returns>
		public float DistancePointSegment(int x, int z, int px, int pz, int qx, int qz)
		{
			float segmentDeltaX = qx - px;
			float segmentDeltaZ = qz - pz;
			float dx = x - px;
			float dz = z - pz;
			float segmentMagnitudeSquared = segmentDeltaX * segmentDeltaX + segmentDeltaZ * segmentDeltaZ;
			float t = segmentDeltaX * dx + segmentDeltaZ * dz;

			//normalize?
			if (segmentMagnitudeSquared > 0)
				t /= segmentMagnitudeSquared;

			//0 < t < 1
			if (t < 0)
				t = 0;
			else if (t > 1)
				t = 1;

			dx = px + t * segmentDeltaX - x;
			dz = pz + t * segmentDeltaZ - z;

			return dx * dx + dz * dz;
		}

		/// <summary>
		/// Clean up the simplified segments
		/// </summary>
		/// <param name="simplified"></param>
		public void RemoveDegenerateSegments(List<int> simplified)
		{
			//remove adjacent vertices which are equal on the xz-plane
			for (int i = 0; i < simplified.Count / 4; i++)
			{
				int ni = i + 1;
				if (ni >= simplified.Count / 4)
					ni = 0;

				if (simplified[i * 4 + 0] == simplified[ni * 4 + 0] &&
					simplified[i * 4 + 2] == simplified[ni * 4 + 2])
				{
					//remove degenerate segment
					for (int j = i; j < simplified.Count / 4 - 1; j++)
					{
						simplified[j * 4 + 0] = simplified[(j + 1) * 4 + 0];
						simplified[j * 4 + 1] = simplified[(j + 1) * 4 + 1];
						simplified[j * 4 + 2] = simplified[(j + 1) * 4 + 2];
						simplified[j * 4 + 3] = simplified[(j + 1) * 4 + 3];
					}
					simplified.RemoveRange(simplified.Count - 4, 4);
				}
			}
		}

		/// <summary>
		/// Determine whether a contour is going forwards (positive area) or backwards (negative area)
		/// </summary>
		/// <param name="verts">The vertex data</param>
		/// <returns></returns>
		public int CalcAreaOfPolygon2D(int[] verts, int numVerts)
		{
			int area = 0;
			for (int i = 0, j = numVerts - 1; i < numVerts; j = i++)
			{
				area += verts[i * 4 + 0] * verts[j * 4 + 2] - verts[j * 4 + 0] * verts[i * 4 + 2];
			}
			return (area + 1) / 2; 
		}

		/// <summary>
		/// Required to find closest indices for merging.
		/// </summary>
		/// <param name="vertsA">First set of vertices</param>
		/// <param name="vertsB">Second set of vertices</param>
		/// <param name="ia">First index</param>
		/// <param name="ib">Second index</param>
		public void GetClosestIndices(int[] vertsA, int numVertsA, int[] vertsB, int numVertsB, ref int ia, ref int ib)
		{
			int closestDistance = 0xfffffff;
			ia = -1;
			ib = -1;
			for (int i = 0; i < numVertsA; i++)
			{
				int iN = (i + 1) % numVertsA;
				int iP = (i + numVertsA - 1) % numVertsA;
				int va = i * 4; //vertsA
				int vaN = iN * 4; //vertsA
				int vaP = iP * 4; //vertsA

				for (int j = 0; j < numVertsB; j++)
				{
					int vb = j * 4; //vertsB
					
					//vb must be infront of va
					if (ILeft(vertsA, vertsB, vaP, va, vb) && ILeft(vertsA, vertsB, va, vaN, vb))
					{
						int dx = vertsB[vb + 0] - vertsA[va + 0];
						int dz = vertsB[vb + 2] - vertsA[va + 2];
						int d = dx * dx + dz * dz;
						if (d < closestDistance)
						{
							ia = i;
							ib = j;
							closestDistance = d;
						}
					}
				}
			}
		}

		/// <summary>
		/// Helper method for GetClosestIndices function
		/// </summary>
		/// <param name="vertsA">First set of vertices</param>
		/// <param name="vertsB">Second set of vertices</param>
		/// <param name="a">First location in vertsA</param>
		/// <param name="b">Second location in vertsA</param>
		/// <param name="c">First location is vertsB</param>
		/// <returns></returns>
		public bool ILeft(int[] vertsA, int[] vertsB, int a, int b, int c)
		{
			return (vertsA[b + 0] - vertsA[a + 0]) * (vertsB[c + 2] - vertsA[a + 2])
				- (vertsB[c + 0] - vertsA[a + 0]) * (vertsA[b + 2] - vertsA[a + 2]) <= 0;
		}

		public void MergeContours(ref Contour contA, Contour contB, int ia, int ib)
		{
			int maxVerts = contA.NumVerts + contB.NumVerts + 2;
			int[] newVerts = new int[maxVerts * 4];
			int nv = 0;

			//copy contour A
			for (int i = 0; i <= contA.NumVerts; i++)
			{
				int dst = nv * 4; //newVerts
				int src = ((ia + 1) % contA.NumVerts) * 4; //contA

				newVerts[dst + 0] = contA.Vertices[src + 0];
				newVerts[dst + 1] = contA.Vertices[src + 1];
				newVerts[dst + 2] = contA.Vertices[src + 2];
				newVerts[dst + 3] = contA.Vertices[src + 3];

				nv++;
			}

			//add contour B (other contour) to contour A (this contour)
			for (int i = 0; i <= contB.NumVerts; i++)
			{
				int dst = nv * 4; //newVerts
				int src = ((ib + i) % contB.NumVerts) * 4; //contB

				newVerts[dst + 0] = contB.Vertices[src + 0];
				newVerts[dst + 1] = contB.Vertices[src + 1];
				newVerts[dst + 2] = contB.Vertices[src + 2];
				newVerts[dst + 3] = contB.Vertices[src + 3];

				nv++;
			}

			contA.Vertices = newVerts;
			contA.NumVerts = nv;
		}

		/// <summary>
		/// A contour is formed from a region.
		/// </summary>
		public class Contour
		{
			//In a group of 4 elements, first 3 store x,y,z coordinates. last one stores index of raw vertex group
			public int [] Vertices;
			public int NumVerts;

			//similar to vertices (simplified), except last element stores region id
			public int [] RawVertices;
			public int NumRawVerts;

			public int RegionId;
			public AreaFlags Area;
		}
	}
}
