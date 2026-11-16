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
 * - Kept the ORCA line construction (static obstacles and agents) and the
 *   linear programs (LinearProgram1/2/3) from Agent.ComputeNewVelocity, plus
 *   the obstacle vertex structure (Obstacle.cs) and its construction from
 *   Simulator.AddObstacle. The Simulator singleton, KdTree and worker threads
 *   were removed; obstacle neighbours are found by a linear scan
 *   (FindObstacleNeighbors) using the same "agent is on the right side" rule
 *   as KdTree.QueryObstacleTreeRecursive.
 * - Each agent neighbour carries a responsibility share (the original always
 *   uses 0.5) so that a moving unit can leave more of the avoidance to an idle
 *   unit.
 * - The agent state is passed in as arguments instead of Agent fields, and the
 *   time step is a parameter instead of Simulator.Instance.TimeStep.
 * - Uses System.Numerics.Vector2 instead of RVO.Vector2 (dot product via
 *   Vector2.Dot), and reuses line buffers to avoid per-call allocations.
 */

using System.Numerics;

namespace OpenRTS.Server.Simulation.Avoidance;

/// <summary>
/// ORCA 가 고려하는 이웃 유닛 상태. Responsibility 는 충돌을 피하는 데 필요한 속도 변화 중
/// 내가 맡는 몫 (원본 RVO2 는 항상 0.5. 두 유닛의 몫을 합하면 1 이 되어야 서로 겹치지 않는다).
/// </summary>
public readonly record struct OrcaNeighbor(Vector2 Position, Vector2 Velocity, float Radius, float Responsibility = 0.5f);

/// <summary>
/// 정적 장애물 다각형의 꼭짓점 하나 (RVO2 Obstacle). 이 꼭짓점에서 <see cref="Next"/> 로 가는 변을 함께 나타낸다.
/// 다각형은 반시계 방향으로 만들며, 바깥은 각 변의 오른쪽이다.
/// </summary>
public sealed class ObstacleVertex
{
    public Vector2 Point { get; private set; }
    public Vector2 Direction { get; private set; }
    public bool Convex { get; private set; }
    public ObstacleVertex Next { get; private set; } = null!;
    public ObstacleVertex Previous { get; private set; } = null!;

    /// <summary>꼭짓점 목록(반시계)으로 닫힌 다각형을 만든다 (RVO2 Simulator.AddObstacle).</summary>
    public static IReadOnlyList<ObstacleVertex> CreatePolygon(IReadOnlyList<Vector2> vertices)
    {
        var result = new List<ObstacleVertex>(vertices.Count);
        for (var i = 0; i < vertices.Count; i++)
        {
            result.Add(new ObstacleVertex { Point = vertices[i] });
        }

        for (var i = 0; i < vertices.Count; i++)
        {
            var next = i == vertices.Count - 1 ? 0 : i + 1;
            var prev = i == 0 ? vertices.Count - 1 : i - 1;
            var v = result[i];
            v.Next = result[next];
            v.Previous = result[prev];
            v.Direction = Vector2.Normalize(vertices[next] - vertices[i]);
            v.Convex = vertices.Count == 2 || LeftOf(vertices[prev], vertices[i], vertices[next]) >= 0.0f;
        }

        return result;
    }

    internal static float LeftOf(Vector2 a, Vector2 b, Vector2 c) => (a.X - c.X) * (b.Y - a.Y) - (a.Y - c.Y) * (b.X - a.X);
}

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
        => ComputeNewVelocity(position, velocity, prefVelocity, radius, maxSpeed, neighbors, timeHorizon, timeStep, Array.Empty<ObstacleVertex>(), 1f);

    /// <summary>
    /// 장애물 변 <paramref name="obstacles"/>(가까운 순, <see cref="FindObstacleNeighbors"/>)과
    /// 이웃 유닛을 함께 고려한다. 장애물 제약은 유닛 제약보다 우선한다 (LinearProgram3 의 numObstLines).
    /// </summary>
    public Vector2 ComputeNewVelocity(
        Vector2 position,
        Vector2 velocity,
        Vector2 prefVelocity,
        float radius,
        float maxSpeed,
        IReadOnlyList<OrcaNeighbor> neighbors,
        float timeHorizon,
        float timeStep,
        IReadOnlyList<ObstacleVertex> obstacles,
        float timeHorizonObst)
    {
        _lines.Clear();
        AddObstacleLines(position, velocity, radius, obstacles, timeHorizonObst);
        var numObstLines = _lines.Count;

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

            line.Point = velocity + other.Responsibility * u;
            _lines.Add(line);
        }

        var lineFail = LinearProgram2(_lines, maxSpeed, prefVelocity, false, out var newVelocity);

        if (lineFail < _lines.Count)
        {
            LinearProgram3(_lines, numObstLines, lineFail, maxSpeed, ref newVelocity);
        }

        return newVelocity;
    }

    /// <summary>
    /// range 안에 있고 유닛이 바깥(오른쪽)에 있는 장애물 변을 가까운 순으로 output 에 담는다.
    /// (RVO2 KdTree.QueryObstacleTreeRecursive + Agent.InsertObstacleNeighbor 를 선형 탐색으로 바꾼 것)
    /// </summary>
    public static void FindObstacleNeighbors(Vector2 position, float range, IReadOnlyList<ObstacleVertex> all, List<ObstacleVertex> output, List<float> distances)
    {
        output.Clear();
        distances.Clear();
        var rangeSq = range * range;
        for (var i = 0; i < all.Count; i++)
        {
            var obstacle1 = all[i];
            var obstacle2 = obstacle1.Next;
            var agentLeftOfLine = ObstacleVertex.LeftOf(obstacle1.Point, obstacle2.Point, position);
            if (agentLeftOfLine >= 0.0f)
            {
                continue; // 변의 안쪽(왼쪽)에서는 보이지 않는다
            }

            var distSq = DistSqPointLineSegment(obstacle1.Point, obstacle2.Point, position);
            if (distSq >= rangeSq)
            {
                continue;
            }

            var k = output.Count;
            output.Add(obstacle1);
            distances.Add(distSq);
            while (k > 0 && distSq < distances[k - 1])
            {
                output[k] = output[k - 1];
                distances[k] = distances[k - 1];
                k--;
            }

            output[k] = obstacle1;
            distances[k] = distSq;
        }
    }

    private static float DistSqPointLineSegment(Vector2 vector1, Vector2 vector2, Vector2 vector3)
    {
        var r = Vector2.Dot(vector3 - vector1, vector2 - vector1) / (vector2 - vector1).LengthSquared();

        if (r < 0.0f)
        {
            return (vector3 - vector1).LengthSquared();
        }

        if (r > 1.0f)
        {
            return (vector3 - vector2).LengthSquared();
        }

        return (vector3 - (vector1 + r * (vector2 - vector1))).LengthSquared();
    }

    /// <summary>장애물 ORCA 선 (RVO2 Agent.ComputeNewVelocity 의 "Create obstacle ORCA lines").</summary>
    private void AddObstacleLines(Vector2 position, Vector2 velocity, float radius, IReadOnlyList<ObstacleVertex> obstacles, float timeHorizonObst)
    {
        var invTimeHorizonObst = 1.0f / timeHorizonObst;

        for (var i = 0; i < obstacles.Count; ++i)
        {
            var obstacle1 = obstacles[i];
            var obstacle2 = obstacle1.Next;

            var relativePosition1 = obstacle1.Point - position;
            var relativePosition2 = obstacle2.Point - position;

            // Check if velocity obstacle of obstacle is already taken care of by
            // previously constructed obstacle ORCA lines.
            var alreadyCovered = false;

            for (var j = 0; j < _lines.Count; ++j)
            {
                if (Det(invTimeHorizonObst * relativePosition1 - _lines[j].Point, _lines[j].Direction) - invTimeHorizonObst * radius >= -Epsilon &&
                    Det(invTimeHorizonObst * relativePosition2 - _lines[j].Point, _lines[j].Direction) - invTimeHorizonObst * radius >= -Epsilon)
                {
                    alreadyCovered = true;
                    break;
                }
            }

            if (alreadyCovered)
            {
                continue;
            }

            // Not yet covered. Check for collisions.
            var distSq1 = relativePosition1.LengthSquared();
            var distSq2 = relativePosition2.LengthSquared();

            var radiusSq = radius * radius;

            var obstacleVector = obstacle2.Point - obstacle1.Point;
            var s = Vector2.Dot(-relativePosition1, obstacleVector) / obstacleVector.LengthSquared();
            var distSqLine = (-relativePosition1 - s * obstacleVector).LengthSquared();

            Line line;

            if (s < 0.0f && distSq1 <= radiusSq)
            {
                // Collision with left vertex. Ignore if non-convex.
                if (obstacle1.Convex)
                {
                    line.Point = Vector2.Zero;
                    line.Direction = Vector2.Normalize(new Vector2(-relativePosition1.Y, relativePosition1.X));
                    _lines.Add(line);
                }

                continue;
            }

            if (s > 1.0f && distSq2 <= radiusSq)
            {
                // Collision with right vertex. Ignore if non-convex or if it will be
                // taken care of by neighboring obstacle.
                if (obstacle2.Convex && Det(relativePosition2, obstacle2.Direction) >= 0.0f)
                {
                    line.Point = Vector2.Zero;
                    line.Direction = Vector2.Normalize(new Vector2(-relativePosition2.Y, relativePosition2.X));
                    _lines.Add(line);
                }

                continue;
            }

            if (s >= 0.0f && s <= 1.0f && distSqLine <= radiusSq)
            {
                // Collision with obstacle segment.
                line.Point = Vector2.Zero;
                line.Direction = -obstacle1.Direction;
                _lines.Add(line);
                continue;
            }

            // No collision. Compute legs. When obliquely viewed, both legs can come
            // from a single vertex. Legs extend cut-off line when non-convex vertex.
            Vector2 leftLegDirection, rightLegDirection;

            if (s < 0.0f && distSqLine <= radiusSq)
            {
                // Obstacle viewed obliquely so that left vertex defines velocity obstacle.
                if (!obstacle1.Convex)
                {
                    continue; // Ignore obstacle.
                }

                obstacle2 = obstacle1;

                var leg1 = MathF.Sqrt(distSq1 - radiusSq);
                leftLegDirection = new Vector2(relativePosition1.X * leg1 - relativePosition1.Y * radius, relativePosition1.X * radius + relativePosition1.Y * leg1) / distSq1;
                rightLegDirection = new Vector2(relativePosition1.X * leg1 + relativePosition1.Y * radius, -relativePosition1.X * radius + relativePosition1.Y * leg1) / distSq1;
            }
            else if (s > 1.0f && distSqLine <= radiusSq)
            {
                // Obstacle viewed obliquely so that right vertex defines velocity obstacle.
                if (!obstacle2.Convex)
                {
                    continue; // Ignore obstacle.
                }

                obstacle1 = obstacle2;

                var leg2 = MathF.Sqrt(distSq2 - radiusSq);
                leftLegDirection = new Vector2(relativePosition2.X * leg2 - relativePosition2.Y * radius, relativePosition2.X * radius + relativePosition2.Y * leg2) / distSq2;
                rightLegDirection = new Vector2(relativePosition2.X * leg2 + relativePosition2.Y * radius, -relativePosition2.X * radius + relativePosition2.Y * leg2) / distSq2;
            }
            else
            {
                // Usual situation.
                if (obstacle1.Convex)
                {
                    var leg1 = MathF.Sqrt(distSq1 - radiusSq);
                    leftLegDirection = new Vector2(relativePosition1.X * leg1 - relativePosition1.Y * radius, relativePosition1.X * radius + relativePosition1.Y * leg1) / distSq1;
                }
                else
                {
                    // Left vertex non-convex; left leg extends cut-off line.
                    leftLegDirection = -obstacle1.Direction;
                }

                if (obstacle2.Convex)
                {
                    var leg2 = MathF.Sqrt(distSq2 - radiusSq);
                    rightLegDirection = new Vector2(relativePosition2.X * leg2 + relativePosition2.Y * radius, -relativePosition2.X * radius + relativePosition2.Y * leg2) / distSq2;
                }
                else
                {
                    // Right vertex non-convex; right leg extends cut-off line.
                    rightLegDirection = obstacle1.Direction;
                }
            }

            // Legs can never point into neighboring edge when convex vertex, take
            // cutoff-line of neighboring edge instead. If velocity projected on
            // "foreign" leg, no constraint is added.
            var leftNeighbor = obstacle1.Previous;

            var isLeftLegForeign = false;
            var isRightLegForeign = false;

            if (obstacle1.Convex && Det(leftLegDirection, -leftNeighbor.Direction) >= 0.0f)
            {
                // Left leg points into obstacle.
                leftLegDirection = -leftNeighbor.Direction;
                isLeftLegForeign = true;
            }

            if (obstacle2.Convex && Det(rightLegDirection, obstacle2.Direction) <= 0.0f)
            {
                // Right leg points into obstacle.
                rightLegDirection = obstacle2.Direction;
                isRightLegForeign = true;
            }

            // Compute cut-off centers.
            var leftCutOff = invTimeHorizonObst * (obstacle1.Point - position);
            var rightCutOff = invTimeHorizonObst * (obstacle2.Point - position);
            var cutOffVector = rightCutOff - leftCutOff;

            // Project current velocity on velocity obstacle.
            // Check if current velocity is projected on cutoff circles.
            var t = obstacle1 == obstacle2 ? 0.5f : Vector2.Dot(velocity - leftCutOff, cutOffVector) / cutOffVector.LengthSquared();
            var tLeft = Vector2.Dot(velocity - leftCutOff, leftLegDirection);
            var tRight = Vector2.Dot(velocity - rightCutOff, rightLegDirection);

            if ((t < 0.0f && tLeft < 0.0f) || (obstacle1 == obstacle2 && tLeft < 0.0f && tRight < 0.0f))
            {
                // Project on left cut-off circle.
                var unitW = Vector2.Normalize(velocity - leftCutOff);

                line.Direction = new Vector2(unitW.Y, -unitW.X);
                line.Point = leftCutOff + radius * invTimeHorizonObst * unitW;
                _lines.Add(line);
                continue;
            }

            if (t > 1.0f && tRight < 0.0f)
            {
                // Project on right cut-off circle.
                var unitW = Vector2.Normalize(velocity - rightCutOff);

                line.Direction = new Vector2(unitW.Y, -unitW.X);
                line.Point = rightCutOff + radius * invTimeHorizonObst * unitW;
                _lines.Add(line);
                continue;
            }

            // Project on left leg, right leg, or cut-off line, whichever is closest
            // to velocity.
            var distSqCutoff = (t < 0.0f || t > 1.0f || obstacle1 == obstacle2) ? float.PositiveInfinity : (velocity - (leftCutOff + t * cutOffVector)).LengthSquared();
            var distSqLeft = tLeft < 0.0f ? float.PositiveInfinity : (velocity - (leftCutOff + tLeft * leftLegDirection)).LengthSquared();
            var distSqRight = tRight < 0.0f ? float.PositiveInfinity : (velocity - (rightCutOff + tRight * rightLegDirection)).LengthSquared();

            if (distSqCutoff <= distSqLeft && distSqCutoff <= distSqRight)
            {
                // Project on cut-off line.
                line.Direction = -obstacle1.Direction;
                line.Point = leftCutOff + radius * invTimeHorizonObst * new Vector2(-line.Direction.Y, line.Direction.X);
                _lines.Add(line);
                continue;
            }

            if (distSqLeft <= distSqRight)
            {
                // Project on left leg.
                if (isLeftLegForeign)
                {
                    continue;
                }

                line.Direction = leftLegDirection;
                line.Point = leftCutOff + radius * invTimeHorizonObst * new Vector2(-line.Direction.Y, line.Direction.X);
                _lines.Add(line);
                continue;
            }

            // Project on right leg.
            if (isRightLegForeign)
            {
                continue;
            }

            line.Direction = -rightLegDirection;
            line.Point = rightCutOff + radius * invTimeHorizonObst * new Vector2(-line.Direction.Y, line.Direction.X);
            _lines.Add(line);
        }
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
