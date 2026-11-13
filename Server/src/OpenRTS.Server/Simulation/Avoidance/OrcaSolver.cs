/*
 * OrcaSolver.cs
 * Adapted from RVO2 Library C# (Agent.cs, RVOMath.cs, Line.cs)
 * https://github.com/snape/RVO2-CS  commit de124a275d51d421bb9e2a77952210527ab60b04
 *
 * SPDX-FileCopyrightText: 2008 University of North Carolina at Chapel Hill
 * SPDX-License-Identifier: Apache-2.0
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 *     http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 *
 * The original authors:
 * Jur van den Berg, Stephen J. Guy, Jamie Snape, Ming C. Lin, Dinesh Manocha
 * Dept. of Computer Science, University of North Carolina at Chapel Hill
 * <http://gamma.cs.unc.edu/RVO2/>
 *
 * Modifications by the OpenRTS project (2026):
 * - Kept only agent-agent ORCA line construction and the linear programs
 *   (LinearProgram1/2/3) from Agent.ComputeNewVelocity. Static obstacle
 *   handling, the Simulator singleton, KdTree and worker threads were removed.
 * - The agent state is passed in as arguments instead of Agent fields, and the
 *   time step is a parameter instead of Simulator.Instance.TimeStep.
 * - Uses System.Numerics.Vector2 instead of RVO.Vector2 (dot product via
 *   Vector2.Dot), and reuses line buffers to avoid per-call allocations.
 */

using System.Numerics;

namespace OpenRTS.Server.Simulation.Avoidance;

/// <summary>ORCA 가 고려하는 이웃 유닛 상태.</summary>
public readonly record struct OrcaNeighbor(Vector2 Position, Vector2 Velocity, float Radius);

/// <summary>
/// ORCA(Optimal Reciprocal Collision Avoidance) 속도 계산. 스레드마다 인스턴스 하나를 쓴다 (내부 버퍼 재사용).
/// </summary>
public sealed class OrcaSolver
{
    private const float Epsilon = 0.00001f;

    private struct Line
    {
        public Vector2 Direction;
        public Vector2 Point;
    }

    private readonly List<Line> _lines = new();
    private readonly List<Line> _projLines = new();

    /// <summary>
    /// 이웃과 <paramref name="timeHorizon"/> 초 안에 충돌하지 않으면서 선호 속도에 가장 가까운 속도를 구한다.
    /// </summary>
    public Vector2 ComputeNewVelocity(
        Vector2 position,
        Vector2 velocity,
        Vector2 prefVelocity,
        float radius,
        float maxSpeed,
        IReadOnlyList<OrcaNeighbor> neighbors,
        float timeHorizon,
        float timeStep)
    {
        _lines.Clear();
        var invTimeHorizon = 1.0f / timeHorizon;

        for (var i = 0; i < neighbors.Count; ++i)
        {
            var other = neighbors[i];
            var relativePosition = other.Position - position;
            var relativeVelocity = velocity - other.Velocity;
            var distSq = relativePosition.LengthSquared();
            var combinedRadius = radius + other.Radius;
            var combinedRadiusSq = combinedRadius * combinedRadius;

            Line line;
            Vector2 u;

            if (distSq > combinedRadiusSq)
            {
                // No collision.
                var w = relativeVelocity - invTimeHorizon * relativePosition;

                // Vector from cutoff center to relative velocity.
                var wLengthSq = w.LengthSquared();
                var dotProduct1 = Vector2.Dot(w, relativePosition);

                if (dotProduct1 < 0.0f && dotProduct1 * dotProduct1 > combinedRadiusSq * wLengthSq)
                {
                    // Project on cut-off circle.
                    var wLength = MathF.Sqrt(wLengthSq);
                    var unitW = w / wLength;

                    line.Direction = new Vector2(unitW.Y, -unitW.X);
                    u = (combinedRadius * invTimeHorizon - wLength) * unitW;
                }
                else
                {
                    // Project on legs.
                    var leg = MathF.Sqrt(distSq - combinedRadiusSq);

                    if (Det(relativePosition, w) > 0.0f)
                    {
                        // Project on left leg.
                        line.Direction = new Vector2(
                            relativePosition.X * leg - relativePosition.Y * combinedRadius,
                            relativePosition.X * combinedRadius + relativePosition.Y * leg) / distSq;
                    }
                    else
                    {
                        // Project on right leg.
                        line.Direction = -new Vector2(
                            relativePosition.X * leg + relativePosition.Y * combinedRadius,
                            -relativePosition.X * combinedRadius + relativePosition.Y * leg) / distSq;
                    }

                    var dotProduct2 = Vector2.Dot(relativeVelocity, line.Direction);
                    u = dotProduct2 * line.Direction - relativeVelocity;
                }
            }
            else
            {
                // Collision. Project on cut-off circle of time timeStep.
                var invTimeStep = 1.0f / timeStep;

                // Vector from cutoff center to relative velocity.
                var w = relativeVelocity - invTimeStep * relativePosition;

                var wLength = w.Length();
                if (wLength < Epsilon)
                {
                    // OpenRTS: 완전히 같은 위치·속도면 방향을 정할 수 없으므로 건너뛴다 (원본은 0으로 나눔).
                    continue;
                }

                var unitW = w / wLength;

                line.Direction = new Vector2(unitW.Y, -unitW.X);
                u = (combinedRadius * invTimeStep - wLength) * unitW;
            }

            line.Point = velocity + 0.5f * u;
            _lines.Add(line);
        }

        var lineFail = LinearProgram2(_lines, maxSpeed, prefVelocity, false, out var newVelocity);

        if (lineFail < _lines.Count)
        {
            LinearProgram3(_lines, 0, lineFail, maxSpeed, ref newVelocity);
        }

        return newVelocity;
    }

    private static float Det(Vector2 a, Vector2 b) => a.X * b.Y - a.Y * b.X;

    /// <summary>Solves a one-dimensional linear program on a specified line
    /// subject to linear constraints defined by lines and a circular constraint.</summary>
    private static bool LinearProgram1(List<Line> lines, int lineNo, float radius, Vector2 optVelocity, bool directionOpt, ref Vector2 result)
    {
        var dotProduct = Vector2.Dot(lines[lineNo].Point, lines[lineNo].Direction);
        var discriminant = dotProduct * dotProduct + radius * radius - lines[lineNo].Point.LengthSquared();

        if (discriminant < 0.0f)
        {
            // Max speed circle fully invalidates line lineNo.
            return false;
        }

        var sqrtDiscriminant = MathF.Sqrt(discriminant);
        var tLeft = -dotProduct - sqrtDiscriminant;
        var tRight = -dotProduct + sqrtDiscriminant;

        for (var i = 0; i < lineNo; ++i)
        {
            var denominator = Det(lines[lineNo].Direction, lines[i].Direction);
            var numerator = Det(lines[i].Direction, lines[lineNo].Point - lines[i].Point);

            if (MathF.Abs(denominator) <= Epsilon)
            {
                // Lines lineNo and i are (almost) parallel.
                if (numerator < 0.0f)
                {
                    return false;
                }

                continue;
            }

            var t = numerator / denominator;

            if (denominator >= 0.0f)
            {
                // Line i bounds line lineNo on the right.
                tRight = Math.Min(tRight, t);
            }
            else
            {
                // Line i bounds line lineNo on the left.
                tLeft = Math.Max(tLeft, t);
            }

            if (tLeft > tRight)
            {
                return false;
            }
        }

        if (directionOpt)
        {
            // Optimize direction.
            if (Vector2.Dot(optVelocity, lines[lineNo].Direction) > 0.0f)
            {
                // Take right extreme.
                result = lines[lineNo].Point + tRight * lines[lineNo].Direction;
            }
            else
            {
                // Take left extreme.
                result = lines[lineNo].Point + tLeft * lines[lineNo].Direction;
            }
        }
        else
        {
            // Optimize closest point.
            var t = Vector2.Dot(lines[lineNo].Direction, optVelocity - lines[lineNo].Point);

            if (t < tLeft)
            {
                result = lines[lineNo].Point + tLeft * lines[lineNo].Direction;
            }
            else if (t > tRight)
            {
                result = lines[lineNo].Point + tRight * lines[lineNo].Direction;
            }
            else
            {
                result = lines[lineNo].Point + t * lines[lineNo].Direction;
            }
        }

        return true;
    }

    /// <summary>Solves a two-dimensional linear program subject to linear
    /// constraints defined by lines and a circular constraint.</summary>
    /// <returns>The number of the line it fails on, and the number of lines if successful.</returns>
    private static int LinearProgram2(List<Line> lines, float radius, Vector2 optVelocity, bool directionOpt, out Vector2 result)
    {
        if (directionOpt)
        {
            // Optimize direction. Note that the optimization velocity is of unit length in this case.
            result = optVelocity * radius;
        }
        else if (optVelocity.LengthSquared() > radius * radius)
        {
            // Optimize closest point and outside circle.
            result = Vector2.Normalize(optVelocity) * radius;
        }
        else
        {
            // Optimize closest point and inside circle.
            result = optVelocity;
        }

        for (var i = 0; i < lines.Count; ++i)
        {
            if (Det(lines[i].Direction, lines[i].Point - result) > 0.0f)
            {
                // Result does not satisfy constraint i. Compute new optimal result.
                var tempResult = result;
                if (!LinearProgram1(lines, i, radius, optVelocity, directionOpt, ref result))
                {
                    result = tempResult;
                    return i;
                }
            }
        }

        return lines.Count;
    }

    /// <summary>Solves a two-dimensional linear program subject to linear
    /// constraints defined by lines and a circular constraint.</summary>
    private void LinearProgram3(List<Line> lines, int numObstLines, int beginLine, float radius, ref Vector2 result)
    {
        var distance = 0.0f;

        for (var i = beginLine; i < lines.Count; ++i)
        {
            if (Det(lines[i].Direction, lines[i].Point - result) > distance)
            {
                // Result does not satisfy constraint of line i.
                _projLines.Clear();
                for (var ii = 0; ii < numObstLines; ++ii)
                {
                    _projLines.Add(lines[ii]);
                }

                for (var j = numObstLines; j < i; ++j)
                {
                    Line line;

                    var determinant = Det(lines[i].Direction, lines[j].Direction);

                    if (MathF.Abs(determinant) <= Epsilon)
                    {
                        // Line i and line j are parallel.
                        if (Vector2.Dot(lines[i].Direction, lines[j].Direction) > 0.0f)
                        {
                            // Line i and line j point in the same direction.
                            continue;
                        }

                        // Line i and line j point in opposite direction.
                        line.Point = 0.5f * (lines[i].Point + lines[j].Point);
                    }
                    else
                    {
                        line.Point = lines[i].Point + (Det(lines[j].Direction, lines[i].Point - lines[j].Point) / determinant) * lines[i].Direction;
                    }

                    line.Direction = Vector2.Normalize(lines[j].Direction - lines[i].Direction);
                    _projLines.Add(line);
                }

                var tempResult = result;
                if (LinearProgram2(_projLines, radius, new Vector2(-lines[i].Direction.Y, lines[i].Direction.X), true, out result) < _projLines.Count)
                {
                    // This should in principle not happen. The result is by definition already in the
                    // feasible region of this linear program. If it fails, it is due to small floating
                    // point error, and the current result is kept.
                    result = tempResult;
                }

                distance = Det(lines[i].Direction, lines[i].Point - result);
            }
        }
    }
}
