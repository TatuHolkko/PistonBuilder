using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.EntityComponents;
using Sandbox.ModAPI.Ingame;
using Sandbox.ModAPI.Interfaces;
using Sandbox.ModAPI.Weapons;
using SpaceEngineers.Game.ModAPI.Ingame;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.ComponentModel;
using System.Linq;
using System.Text;
using VRage;
using VRage.Collections;
using VRage.Game;
using VRage.Game.Components;
using VRage.Game.GUI.TextPanel;
using VRage.Game.ModAPI.Ingame;
using VRage.Game.ModAPI.Ingame.Utilities;
using VRage.Game.ObjectBuilders.Definitions;
using VRageMath;

namespace IngameScript
{
    /// <summary>
    /// This script controls a 3D welder using pistons and hinges to position the welder head.
    /// Each corner of the welder area has a vertical piston and a number of horizontal pistons.
    /// The welder head is attached to the horizontal pistons via four free hinges.
    /// Vertical pistons control the height of the welder head by extending or retracting in unison.
    /// Horizontal pistons control the X and Y position of the welder head by controlling the individual
    /// distance from each corner.
    /// </summary>
    public partial class Program : MyGridProgram
    {
        /// DO NOT CHANGE ABOVE THIS LINE ///

        // Number of blocks between horizontal base hinges (including the corners)
        // from which the horizontal pistons extend towards the welder
        const int areaWidth = 17;
        const int areaHeight = 17;

        // Number of pistons per corner that connect to the welder
        const int numHorizontalPistons = 2;
        // Number of vertical pistons per corner that lift the welder
        const int numVerticalPistons = 2;

        // Vertical total height limits, will be divided equally among vertical pistons
        const float ceilingHeight = 20f; // meters
        const float floorHeight = 2.5f; // meters

        // Naming conventions
        const string namePrefix = "[BP] ";
        const string pistonNameBase = "Pist: ";
        const string hingeNameBase = "Hing: ";
        const string lightNameBase = "Lght: ";
        const string sensorNameBase = "Sens: ";
        const string topTag = "T";
        const string bottomTag = "B";
        const string leftTag = "L";
        const string rightTag = "R";
        const string verticalTag = "V";
        const string horizontalTag = "H";
        const string baseTag = "B";
        const string welderTag = "W";
        const string welderName = "Welder";

        const string debugLCDBlockName = "Control Seat";
        const int debugLCDSurfaceIndex = 0;

        const string mapLCDBlockName = "Control Seat";
        const int mapLCDSurfaceIndex = 1;

        /// DO NOT CHANGE BELOW THIS LINE ///

        const float cubeSize = 2.5f; // meters
        const float retractedPistonTotalLength = 2 * cubeSize + 0.159f; // meters
        const int welderSize = 1; // blocks in each dimension from the center, e.g. 1 = 3x3
        const float verticalPistonVelocity = 1f; // meters per second
        const float horizontalPistonVelocity = 1f; // meters per second
        const float maxZ = (ceilingHeight - floorHeight) / cubeSize; // maximum welder Z height in blocks
        enum Corner
        {
            TopLeft,
            TopRight,
            BottomLeft,
            BottomRight
        }
        enum Side
        {
            Top,
            Bottom,
            Left,
            Right
        }
        List<Corner> corners = new List<Corner> { Corner.TopLeft, Corner.TopRight, Corner.BottomLeft, Corner.BottomRight };
        List<Side> sides = new List<Side> { Side.Top, Side.Bottom, Side.Left, Side.Right };
        Dictionary<Corner, List<IMyPistonBase>> horizontalPistonLists = new Dictionary<Corner, List<IMyPistonBase>>();
        Dictionary<Corner, List<IMyPistonBase>> verticalPistonLists = new Dictionary<Corner, List<IMyPistonBase>>();
        Dictionary<Corner, IMyMotorStator> baseHinges = new Dictionary<Corner, IMyMotorStator>();
        Dictionary<Corner, IMyMotorStator> welderHinges = new Dictionary<Corner, IMyMotorStator>();
        Dictionary<Side, IMySensorBlock> welderSensors = new Dictionary<Side, IMySensorBlock>();
        Dictionary<Side, float> sensorReadings = new Dictionary<Side, float>()
        {
            { Side.Top, -1f },
            { Side.Bottom, -1f },
            { Side.Left, -1f },
            { Side.Right, -1f }
        };
        float sensorRange = 0; // meters
        List<IMyLightingBlock> statusLights = new List<IMyLightingBlock>();
        class TaskItem
        {
            public Func<float> execute = () => -1f; // returns time required to complete task
            public Action finish = () => { }; // called when task is finished
            public string description = "Null task"; // description for debug screen
        }
        Queue<TaskItem> taskQueue = new Queue<TaskItem>();
        TaskItem currentTask = new TaskItem();
        IMyShipWelder welder;

        IMyTextSurface debugScreen;
        IMyTextSurface mapScreen;
        IMyTextSurface pbScreen;
        List<List<bool>> reachableGridPoints = new List<List<bool>>();
        // Height map of scanned area, -1 = unscanned, -2 = unreachable
        List<List<float>> heightMap = new List<List<float>>();

        // Last known welder position
        float welderX = -1; // blocks from top left corner
        float welderY = -1; // blocks from top left corner
        float welderZ = -1f; // blocks from base level

        // When pistons are moving, the target position
        float welderTargetX = -1; // blocks from top left corner
        float welderTargetY = -1; // blocks from top left corner
        float welderTargetZ = -1f; // blocks from base level
        float taskTimeLeft = 0f;

        bool heightScanInProgress = false;
        bool scanDownInProgress = false;
        bool safetyLock = true;

        public Program()
        {
            Runtime.UpdateFrequency = UpdateFrequency.Update10;
            horizontalPistonLists = new Dictionary<Corner, List<IMyPistonBase>>()
            {
                { Corner.TopLeft, GetHorizontalPistons(namePrefix + pistonNameBase + horizontalTag + topTag + leftTag) },
                { Corner.TopRight, GetHorizontalPistons(namePrefix + pistonNameBase + horizontalTag + topTag + rightTag) },
                { Corner.BottomLeft, GetHorizontalPistons(namePrefix + pistonNameBase + horizontalTag + bottomTag + leftTag) },
                { Corner.BottomRight, GetHorizontalPistons(namePrefix + pistonNameBase + horizontalTag + bottomTag + rightTag) }
            };
            verticalPistonLists = new Dictionary<Corner, List<IMyPistonBase>>()
            {
                { Corner.TopLeft, GetVerticalPistons(namePrefix + pistonNameBase + verticalTag + topTag + leftTag) },
                { Corner.TopRight, GetVerticalPistons(namePrefix + pistonNameBase + verticalTag + topTag + rightTag) },
                { Corner.BottomLeft, GetVerticalPistons(namePrefix + pistonNameBase + verticalTag + bottomTag + leftTag) },
                { Corner.BottomRight, GetVerticalPistons(namePrefix + pistonNameBase + verticalTag + bottomTag + rightTag) }
            };
            baseHinges = new Dictionary<Corner, IMyMotorStator>()
            {
                { Corner.TopLeft, GetHinge(namePrefix + hingeNameBase + baseTag + topTag + leftTag) as IMyMotorStator },
                { Corner.TopRight, GetHinge(namePrefix + hingeNameBase + baseTag + topTag + rightTag) as IMyMotorStator },
                { Corner.BottomLeft, GetHinge(namePrefix + hingeNameBase + baseTag + bottomTag + leftTag) as IMyMotorStator },
                { Corner.BottomRight, GetHinge(namePrefix + hingeNameBase + baseTag + bottomTag + rightTag) as IMyMotorStator }
            };
            welderHinges = new Dictionary<Corner, IMyMotorStator>()
            {
                { Corner.TopLeft, GetHinge(namePrefix + hingeNameBase + welderTag + topTag + leftTag) as IMyMotorStator },
                { Corner.TopRight, GetHinge(namePrefix + hingeNameBase + welderTag + topTag + rightTag) as IMyMotorStator },
                { Corner.BottomLeft, GetHinge(namePrefix + hingeNameBase + welderTag + bottomTag + leftTag) as IMyMotorStator },
                { Corner.BottomRight, GetHinge(namePrefix + hingeNameBase + welderTag + bottomTag + rightTag) as IMyMotorStator }
            };
            welderSensors = new Dictionary<Side, IMySensorBlock>()
            {
                { Side.Top, GetSensor(namePrefix + sensorNameBase + welderTag + topTag) as IMySensorBlock },
                { Side.Bottom, GetSensor(namePrefix + sensorNameBase + welderTag + bottomTag) as IMySensorBlock },
                { Side.Left, GetSensor(namePrefix + sensorNameBase + welderTag + leftTag) as IMySensorBlock },
                { Side.Right, GetSensor(namePrefix + sensorNameBase + welderTag + rightTag) as IMySensorBlock }
            };
            statusLights.Add(GetBlock(namePrefix + lightNameBase + welderTag + topTag) as IMyLightingBlock);
            statusLights.Add(GetBlock(namePrefix + lightNameBase + welderTag + bottomTag) as IMyLightingBlock);
            welderZ = GetAverageHeight();
            welder = GetBlock(namePrefix + welderName) as IMyShipWelder;
            pbScreen = Me.GetSurface(0);
            debugScreen = (GetBlock(namePrefix + debugLCDBlockName) as IMyTextSurfaceProvider).GetSurface(debugLCDSurfaceIndex);
            mapScreen = (GetBlock(namePrefix + mapLCDBlockName) as IMyTextSurfaceProvider).GetSurface(mapLCDSurfaceIndex);
            pbScreen = Me.GetSurface(0);
            InitHeightMap();
            ScanReachable();
            if (welderX == -1 || welderY == -1)
            {
                throw new Exception("No reachable welder position found!");
            }
        }

        /// <summary>
        /// Scans the welder area grid to determine which points are reachable by the welder
        /// based on the piston extension limits. Also finds the closest reachable point to the
        /// real welder position.
        /// </summary>
        void ScanReachable()
        {
            float closestDistance = float.MaxValue;
            for (int y = 0; y < areaHeight; y++)
            {
                reachableGridPoints.Add(new List<bool>());
                for (int x = 0; x < areaWidth; x++)
                {
                    Dictionary<Corner, float> extensions = PistonExtensions(x, y);
                    bool reachable = true;
                    float distSum = 0f;
                    foreach (Corner corner in corners)
                    {
                        float extension = extensions[corner];
                        if (extension < 0f || extension > 10f)
                        {
                            reachable = false;
                            break;
                        }
                        foreach (IMyPistonBase piston in horizontalPistonLists[corner])
                        {
                            distSum += (float)Math.Pow(Math.Abs(piston.CurrentPosition - extension), 2);
                        }
                    }
                    if (distSum < closestDistance && reachable)
                    {
                        closestDistance = distSum;
                        welderX = x;
                        welderY = y;
                    }
                    reachableGridPoints[y].Add(reachable);
                    heightMap[y][x] = -2f;
                }
            }
        }
        void DebugFindClosestGridByPistonExtensions()
        {
            float closestDistance = float.MaxValue;
            int bestX = -1;
            int bestY = -1;

            for (int y = 0; y < areaHeight; y++)
            {
                for (int x = 0; x < areaWidth; x++)
                {
                    Dictionary<Corner, float> extensions = PistonExtensions(x, y);
                    bool valid = true;
                    float distSum = 0f;
                    foreach (Corner corner in corners)
                    {
                        float extension = extensions[corner];
                        if (extension < 0f || extension > 10f)
                        {
                            valid = false;
                            break;
                        }
                        foreach (IMyPistonBase piston in horizontalPistonLists[corner])
                        {
                            float diff = piston.CurrentPosition - extension;
                            distSum += diff * diff;
                        }
                    }
                    if (!valid) continue;
                    if (distSum < closestDistance)
                    {
                        closestDistance = distSum;
                        bestX = x;
                        bestY = y;
                    }
                }
            }

            if (bestX == -1)
            {
                Echo("DebugFindClosestGrid: no valid grid point found (all extensions out of range)");
                return;
            }

            double gridDistance = Math.Sqrt(Math.Pow(bestX - welderX, 2) + Math.Pow(bestY - welderY, 2));
            Echo($"DebugFindClosestGrid: closest = ({bestX}, {bestY}) with mismatch {closestDistance:F4}");
            Echo($"Current welder pos = ({welderX}, {welderY}), grid distance = {gridDistance:F2}");
        }

        void InitHeightMap()
        {
            heightMap.Clear();
            for (int y = 0; y < areaHeight; y++)
            {
                heightMap.Add(new List<float>());
                for (int x = 0; x < areaWidth; x++)
                {
                    heightMap[y].Add(-1f);
                }
            }
        }

        bool IsReachable(float targetX, float targetY, float targetZ)
        {
            float tolerance = 0.01f;
            int xInt = (int)Math.Round(targetX);
            int yInt = (int)Math.Round(targetY);
            float deltaX = targetX - xInt;
            float deltaY = targetY - yInt;
            if (xInt < 0 || xInt >= areaWidth || yInt < 0 || yInt >= areaHeight)
            {
                return false;
            }
            if (deltaX > 0 + tolerance)
            {
                if (xInt + 1 >= areaWidth || !reachableGridPoints[yInt][xInt + 1])
                {
                    Echo("X+ out of bounds or unreachable");
                    return false;
                }
            }
            else if (deltaX < 0 - tolerance)
            {
                if (xInt - 1 < 0 || !reachableGridPoints[yInt][xInt - 1])
                {
                    Echo("X- out of bounds or unreachable");
                    return false;
                }
            }
            if (deltaY > 0 + tolerance)
            {
                if (yInt + 1 >= areaHeight || !reachableGridPoints[yInt + 1][xInt])
                {
                    Echo("Y+ out of bounds or unreachable");
                    return false;
                }
            }
            else if (deltaY < 0 - tolerance)
            {
                if (yInt - 1 < 0 || !reachableGridPoints[yInt - 1][xInt])
                {
                    Echo("Y- out of bounds or unreachable");
                    return false;
                }
            }
            float perPistonHeight = (targetZ * cubeSize + floorHeight) / numVerticalPistons;
            return reachableGridPoints[xInt][yInt] &&
                   perPistonHeight >= floorHeight && perPistonHeight <= ceilingHeight;
        }

        float GetAverageHeight()
        {
            float totalHeight = 0f;
            foreach (Corner corner in corners)
            {
                foreach (IMyPistonBase piston in verticalPistonLists[corner])
                {
                    totalHeight += piston.CurrentPosition - (floorHeight / numVerticalPistons);
                }
            }
            return totalHeight / (corners.Count) / cubeSize;
        }



        List<IMyPistonBase> GetVerticalPistons(string name)
        {
            List<IMyPistonBase> pistons = new List<IMyPistonBase>();
            for (int i = 0; i < numVerticalPistons; i++)
            {
                pistons.Add(GetBlock(name + " " + (i + 1)) as IMyPistonBase);
                InitVerticalPiston(pistons[i]);
            }
            return pistons;
        }

        List<IMyPistonBase> GetHorizontalPistons(string name)
        {
            List<IMyPistonBase> pistons = new List<IMyPistonBase>();
            for (int i = 0; i < numHorizontalPistons; i++)
            {
                pistons.Add(GetBlock(name + " " + (i + 1)) as IMyPistonBase);
                InitHorizontalPiston(pistons[i]);
            }
            return pistons;
        }

        IMyMotorStator GetHinge(string name)
        {
            IMyMotorStator hinge = GetBlock(name) as IMyMotorStator;
            InitHinge(hinge);
            return hinge;
        }

        IMySensorBlock GetSensor(string name)
        {
            IMySensorBlock sensor = GetBlock(name) as IMySensorBlock;
            InitSensor(sensor);
            return sensor;
        }

        IMyTerminalBlock GetBlock(string name)
        {
            IMyTerminalBlock block = GridTerminalSystem.GetBlockWithName(name);
            if (block == null)
            {
                throw new Exception("Could not find block: " + name);
            }
            return block;
        }

        void InitVerticalPiston(IMyPistonBase piston)
        {
            float minPerPistonHeight = floorHeight / numVerticalPistons;
            float maxPerPistonHeight = ceilingHeight / numVerticalPistons;
            piston.MinLimit = minPerPistonHeight;
            piston.MaxLimit = maxPerPistonHeight;
            piston.Velocity = 0;
            piston.Enabled = true;
        }

        void InitHorizontalPiston(IMyPistonBase piston)
        {
            piston.MinLimit = 0.0f;
            piston.MaxLimit = 10.0f;
            piston.Velocity = 0;
            piston.Enabled = true;
        }

        void InitHinge(IMyMotorStator hinge)
        {
            hinge.TargetVelocityRPM = 0f;
            hinge.LowerLimitDeg = -90f;
            hinge.UpperLimitDeg = 90f;
            hinge.Enabled = false;
        }

        void InitSensor(IMySensorBlock sensor)
        {
            sensor.Enabled = true;

            sensor.FrontExtend = sensorRange;
            sensor.BackExtend = 0f;
            sensor.LeftExtend = 0f;
            sensor.RightExtend = 0f;
            sensor.TopExtend = 0f;
            sensor.BottomExtend = 0f;

            sensor.DetectSubgrids = true;
            sensor.DetectLargeShips = true;
            sensor.DetectSmallShips = true;
            sensor.DetectStations = true;

            sensor.DetectFloatingObjects = false;
            sensor.DetectAsteroids = false;
            sensor.DetectPlayers = false;
        }

        Dictionary<Corner, float> PistonExtensions(float welderX, float welderY)
        {
            Dictionary<Corner, float> locations = new Dictionary<Corner, float>();
            locations[Corner.TopLeft] = GetPistonExtension(welderX - welderSize, welderY - welderSize);
            locations[Corner.TopRight] = GetPistonExtension((areaWidth - 1 - welderX) - welderSize, welderY - welderSize);
            locations[Corner.BottomLeft] = GetPistonExtension(welderX - welderSize, (areaHeight - 1 - welderY) - welderSize);
            locations[Corner.BottomRight] = GetPistonExtension((areaWidth - 1 - welderX) - welderSize, (areaHeight - 1 - welderY) - welderSize);
            return locations;
        }

        float GetPistonExtension(float gridDistX, float gridDistY)
        {
            float metersBetweenHinges = (float)Math.Sqrt(gridDistX * gridDistX + gridDistY * gridDistY) * cubeSize - cubeSize;
            float perPistonExtension = metersBetweenHinges / numHorizontalPistons - retractedPistonTotalLength;
            return perPistonExtension;
        }

        float ExecuteEqualizePistonGroup(List<IMyPistonBase> pistons, float time, float speedLimit)
        {
            float realTime = 0f;
            float totalExtension = 0f;
            foreach (IMyPistonBase piston in pistons)
            {
                totalExtension += piston.CurrentPosition;
            }
            float averageExtension = totalExtension / pistons.Count;
            foreach (IMyPistonBase piston in pistons)
            {
                float pistonMovementTime = ExecuteMovePiston(piston, averageExtension, time, speedLimit);
                if (pistonMovementTime > realTime)
                {
                    realTime = pistonMovementTime;
                }
            }
            return realTime;
        }

        float ExecuteMoveTo(float targetX, float targetY, float targetZ, float time, bool ignoreSpeedLimits = false)
        {
            if (!IsReachable(targetX, targetY, targetZ))
            {
                throw new Exception("Target position (" + targetX + ", " + targetY + ", " + targetZ + ") is not reachable!");
            }
            if (safetyLock)
            {
                throw new Exception("Safety lock is enabled, cannot move welder! Unlock to proceed.");
            }
            float maxTime = 0f;
            float perPistonHeight = (targetZ * cubeSize + floorHeight) / numVerticalPistons;
            Dictionary<Corner, float> extensions = PistonExtensions(targetX, targetY);
            foreach (Corner corner in corners)
            {
                float horizontalExtension = extensions[corner];
                foreach (IMyPistonBase piston in horizontalPistonLists[corner])
                {
                    float pistonTime = ExecuteMovePiston(piston, horizontalExtension, time, ignoreSpeedLimits ? float.MaxValue : horizontalPistonVelocity);
                    if (pistonTime > maxTime)
                    {
                        maxTime = pistonTime;
                    }
                }
                foreach (IMyPistonBase piston in verticalPistonLists[corner])
                {
                    float pistonTime = ExecuteMovePiston(piston, perPistonHeight, time, ignoreSpeedLimits ? float.MaxValue : verticalPistonVelocity);
                    if (pistonTime > maxTime)
                    {
                        maxTime = pistonTime;
                    }
                }
            }
            welderTargetX = targetX;
            welderTargetY = targetY;
            welderTargetZ = targetZ;
            return maxTime;
        }

        void QueueEqualizeAllPistons(float time)
        {
            foreach (Corner corner in corners)
            {
                taskQueue.Enqueue(new TaskItem
                {
                    execute = () => ExecuteEqualizePistonGroup(verticalPistonLists[corner], time, verticalPistonVelocity),
                    description = "Equalizing vertical pistons at " + corner.ToString() + " in target time " + time + " s"
                });
                taskQueue.Enqueue(new TaskItem
                {
                    execute = () => ExecuteEqualizePistonGroup(horizontalPistonLists[corner], time, horizontalPistonVelocity),
                    description = "Equalizing piston group: " + corner.ToString(),
                });
            }
        }

        float ExecuteMovePiston(IMyPistonBase piston, float targetPosition, float time, float speedLimit)
        {
            float actualTime = time;
            float deltaDistance = targetPosition - piston.CurrentPosition;
            float velocity = deltaDistance / time;
            if (Math.Abs(velocity) > speedLimit)
            {
                velocity = Math.Sign(velocity) * speedLimit;
                actualTime = deltaDistance / velocity;
            }
            if (velocity < 0)
            {
                piston.MinLimit = targetPosition;
                piston.MaxLimit = piston.CurrentPosition;
            }
            else
            {
                piston.MinLimit = piston.CurrentPosition;
                piston.MaxLimit = targetPosition;
            }
            piston.Velocity = velocity;
            return actualTime;
        }

        void InitiateScanDownProcess()
        {
            sensorReadings[Side.Top] = -1f;
            sensorReadings[Side.Bottom] = -1f;
            sensorReadings[Side.Left] = -1f;
            sensorReadings[Side.Right] = -1f;
            sensorRange = 0f;
            welderSensors[Side.Top].FrontExtend = sensorRange;
            welderSensors[Side.Bottom].FrontExtend = sensorRange;
            welderSensors[Side.Left].FrontExtend = sensorRange;
            welderSensors[Side.Right].FrontExtend = sensorRange;
            scanDownInProgress = true;
        }

        bool ReadSensors()
        {
            bool allSidesMeasured = true;
            foreach (Side side in sides)
            {
                if (sensorReadings[side] >= 0f)
                {
                    continue;
                }
                allSidesMeasured = false;
                IMySensorBlock sensor = welderSensors[side];
                if (sensor.IsActive)
                {
                    float detectedAt = welderZ - sensorRange;
                    sensorReadings[side] = detectedAt;
                }
                else
                {
                    sensorRange += 0.5f;
                    if (sensorRange > welderZ)
                    {
                        sensorReadings[side] = 0; // No obstacle detected within range
                        continue;
                    }
                    sensor.FrontExtend = sensorRange;
                }
            }
            return allSidesMeasured;
        }

        bool ScanDownStep()
        {
            if (ReadSensors())
            {
                int welderX = (int)Math.Round(this.welderX);
                int welderY = (int)Math.Round(this.welderY);
                List<int> point = new List<int> { welderX + 1, welderY };
                heightMap[point[1]][point[0]] = sensorReadings[Side.Right];
                point = new List<int> { welderX - 1, welderY };
                heightMap[point[1]][point[0]] = sensorReadings[Side.Left];
                point = new List<int> { welderX, welderY + 1 };
                heightMap[point[1]][point[0]] = sensorReadings[Side.Bottom];
                point = new List<int> { welderX, welderY - 1 };
                heightMap[point[1]][point[0]] = sensorReadings[Side.Top];
                return true;
            }
            return false;
        }

        bool HeightScanStep()
        {
            if (scanDownInProgress)
            {
                if (ScanDownStep())
                {
                    scanDownInProgress = false;
                }
                else
                {
                    return false;
                }
            }
            List<int> nextPoint = GetClosestUnmeasuredPoint();
            if (nextPoint[0] == -1 || nextPoint[1] == -1)
            {
                Echo("Height scan complete.");
                heightScanInProgress = false;
                return true;
            }
            List<int> adjacentPoint = GetClosestMeasuredAdjacentPoint(nextPoint[0], nextPoint[1]);

            QueueMove(adjacentPoint[0], adjacentPoint[1], welderZ, 1f,
                description: $"Moving to adjacent measured point ({adjacentPoint[0]}, {adjacentPoint[1]})",
                onFinish: InitiateScanDownProcess);
            return false;
        }

        void InitiateHeightScanProcess()
        {
            heightScanInProgress = true;
            InitHeightMap();
            InitiateScanDownProcess();
        }

        List<int> GetClosestUnmeasuredPoint()
        {
            List<int> point = new List<int> { -1, -1 };
            float closestDistance = float.MaxValue;
            for (int y = 0; y < areaHeight; y++)
            {
                for (int x = 0; x < areaWidth; x++)
                {
                    if (heightMap[y][x] >= 0f)
                    {
                        continue;
                    }
                    if (!reachableGridPoints[y][x])
                    {
                        continue;
                    }
                    float dist = (float)Math.Sqrt(Math.Pow(x - welderX, 2) + Math.Pow(y - welderY, 2));
                    if (dist < closestDistance)
                    {
                        closestDistance = dist;
                        point[0] = x;
                        point[1] = y;
                    }
                }
            }
            return point;
        }

        List<int> GetClosestMeasuredAdjacentPoint(int x, int y)
        {
            List<int> point = new List<int> { -1, -1 };
            List<int> defaultPoint = new List<int> { -1, -1 };
            float closestDistance = float.MaxValue;
            bool allUnmeasured = true;
            List<List<int>> adjacentPoints = new List<List<int>>()
            {
                new List<int> { x - 1, y },
                new List<int> { x + 1, y },
                new List<int> { x, y - 1 },
                new List<int> { x, y + 1 }
            };
            foreach (List<int> adjacentPoint in adjacentPoints)
            {
                int adjX = adjacentPoint[0];
                int adjY = adjacentPoint[1];
                if (adjX < 0 || adjX >= areaWidth || adjY < 0 || adjY >= areaHeight)
                {
                    continue;
                }
                if (!reachableGridPoints[adjY][adjX])
                {
                    continue;
                }
                defaultPoint[0] = adjX;
                defaultPoint[1] = adjY;
                if (heightMap[adjY][adjX] < 0f)
                {
                    continue;
                }
                allUnmeasured = false;
                float dist = (float)Math.Sqrt(Math.Pow(adjX - welderX, 2) + Math.Pow(adjY - welderY, 2));
                if (dist < closestDistance)
                {
                    closestDistance = dist;
                    point[0] = adjX;
                    point[1] = adjY;
                }
            }
            if (allUnmeasured)
            {
                return defaultPoint;
            }
            return point;
        }

        void Abort()
        {
            foreach (Corner corner in corners)
            {
                foreach (IMyPistonBase piston in horizontalPistonLists[corner])
                {
                    piston.Velocity = 0f;
                }
                foreach (IMyPistonBase piston in verticalPistonLists[corner])
                {
                    piston.Velocity = 0f;
                }
            }
            welderTargetX = welderX;
            welderTargetY = welderY;
            welderTargetZ = welderZ;
            heightScanInProgress = false;
            scanDownInProgress = false;
            taskTimeLeft = 0f;
            taskQueue.Clear();
        }
        /// <summary>
        /// Calculates the accuracy of the current welder position compared to the target position
        /// based on the piston extensions.
        /// </summary>
        /// <param name="x">X target position</param>
        /// <param name="y">Y target position</param>
        /// <param name="z">Z target position</param>
        /// <returns>Accuracy as a float between 0 and 1</returns>
        float CalculateAccuracy(float x, float y, float z)
        {
            float totalCalculatedDistance = 0f;
            float error = 0f;
            float perPistonHeight = (z * cubeSize + floorHeight) / numVerticalPistons;
            Dictionary<Corner, float> extensions = PistonExtensions(x, y);
            foreach (Corner corner in corners)
            {
                float targetExtension = extensions[corner];
                foreach (IMyPistonBase piston in horizontalPistonLists[corner])
                {
                    totalCalculatedDistance += Math.Abs(targetExtension);
                    error += Math.Abs(piston.CurrentPosition - targetExtension);
                }
                foreach (IMyPistonBase piston in verticalPistonLists[corner])
                {
                    totalCalculatedDistance += perPistonHeight;
                    error += Math.Abs(piston.CurrentPosition - perPistonHeight);
                }
            }
            return 1f - error / totalCalculatedDistance;
        }

        bool TryUnlockSafety()
        {
            if (NeedsInitialization())
            {
                return false;
            }
            safetyLock = false;
            return true;
        }

        void LockSafety()
        {
            Abort();
            safetyLock = true;
        }

        void ParseMoveCommand(string argument)
        {
            string[] parts = argument.Substring(5).Split(' ');
            if (parts.Length != 4)
            {
                Echo("Invalid move command format! Use: move <x> <y> <z> <time>");
                return;
            }
            int targetX = int.Parse(parts[0]);
            int targetY = int.Parse(parts[1]);
            float targetZ = float.Parse(parts[2]);
            float time = float.Parse(parts[3]);
            if (!IsReachable(targetX, targetY, targetZ))
            {
                Echo("Target position (" + targetX + ", " + targetY + ", " + targetZ + ") is not reachable!");
                return;
            }
            QueueMove(targetX, targetY, targetZ, time);
        }

        List<List<float>> GetTravelPoints(float startX, float startY, float startZ, float targetX, float targetY, float targetZ, float stepSize = 2f)
        {
            List<List<float>> points = new List<List<float>>();
            float deltaX = targetX - startX;
            float deltaY = targetY - startY;
            float deltaZ = targetZ - startZ;
            int steps = (int)Math.Ceiling(Math.Sqrt(deltaX * deltaX + deltaY * deltaY) / stepSize);
            for (int i = 1; i <= steps; i++)
            {
                float t = (float)i / steps;
                float interpX = startX + deltaX * t;
                float interpY = startY + deltaY * t;
                float interpZ = startZ + deltaZ * t;
                points.Add(new List<float> { interpX, interpY, interpZ });
            }
            return points;
        }

        void QueueMove(int targetX, int targetY, float targetZ, float time, string description = "", Action onFinish = null)
        {
            List<List<float>> travelPoints = GetTravelPoints(welderX, welderY, welderZ, targetX, targetY, targetZ);
            float perPointTime = time / travelPoints.Count;
            int i = 1;
            Echo($"Current welder position: ({welderX}, {welderY}, {welderZ})");
            Echo($"Queuing move to ({targetX}, {targetY}, {targetZ}) in {time} s over {travelPoints.Count} points.");
            Echo("Travel points:");
            foreach (List<float> point in travelPoints)
            {
                Echo($"  Point {i}: ({point[0]}, {point[1]}, {point[2]})");
                i++;
            }
            i = 1;
            foreach (List<float> point in travelPoints)
            {
                float pointX = point[0];
                float pointY = point[1];
                float pointZ = point[2];
                string defDesc = $"moving to midpoint ({pointX}, {pointY}, {pointZ}) towards ({targetX}, {targetY}, {targetZ})";
                taskQueue.Enqueue(new TaskItem
                {
                    execute = () => ExecuteMoveTo(pointX, pointY, pointZ, perPointTime),
                    description = $"({i}/{travelPoints.Count}) " + (description == "" ? defDesc : description)
                });
                i++;
            }
            taskQueue.Enqueue(new TaskItem
            {
                execute = () => 0f,
                finish = () =>
                {
                    welderX = targetX;
                    welderY = targetY;
                    welderZ = targetZ;
                    onFinish?.Invoke();
                },
            });
        }

        void ParseCommand(string argument)
        {
            if (argument == "init")
            {
                if (heightScanInProgress)
                {
                    Echo("Cannot initialize pistons: Height scan in progress!");
                    return;
                }
                InitTasks();
            }
            else if (argument == "unlock")
            {
                if (!TryUnlockSafety())
                {
                    Echo("Cannot unlock safety lock: Pistons need initialization! Run 'init' command first.");
                    return;
                }
                Echo("Safety lock disabled.");
            }
            else if (argument == "lock")
            {
                LockSafety();
                Echo("Safety lock enabled.");
            }
            else if (argument.StartsWith("move "))
            {
                if (safetyLock)
                {
                    Echo("Cannot move welder: Safety lock is enabled! Run 'unlock' to proceed.");
                    return;
                }
                if (heightScanInProgress)
                {
                    Echo("Cannot move welder: Height scan in progress!");
                    return;
                }
                ParseMoveCommand(argument);
                Echo("Queued move command: " + argument);
            }
            else if (argument == "abort")
            {
                Echo("Aborting movement.");
                Abort();
            }
            else if (argument == "heightscan")
            {
                if (safetyLock)
                {
                    Echo("Cannot start scan: Safety lock is enabled! Run 'unlock' to proceed.");
                    return;
                }
                if (heightScanInProgress)
                {
                    Echo("Height scan already in progress!");
                    return;
                }
                Echo("Initiated height scan process.");
                InitiateHeightScanProcess();
            }
            else
            {
                Echo("Unknown command: " + argument);
            }
        }

        void PrintMap()
        {
            mapScreen.WriteText("Welder Reachability Map\n");
            for (int y = 0; y < areaHeight; y++)
            {
                string line = "";
                for (int x = 0; x < areaWidth; x++)
                {
                    if (x == welderX && y == welderY)
                    {
                        line += "W ";
                    }
                    else if (x == welderTargetX && y == welderTargetY)
                    {
                        line += "T ";
                    }
                    else if (reachableGridPoints[y][x])
                    {
                        if (heightMap[y][x] >= 0f)
                        {
                            line += Math.Round(heightMap[y][x]).ToString() + " ";
                        }
                        else
                        {
                            line += ". ";
                        }
                    }
                    else
                    {
                        line += "- ";
                    }
                }
                mapScreen.WriteText(line + "\n", true);
            }
        }

        void DebugScreen(IMyTextSurface screen)
        {
            screen.WriteText("Welder Control Debug Screen\n", true);
            screen.WriteText($"Safety Lock: {(safetyLock ? "ENGAGED" : "DISENGAGED")}\n", true);
            screen.WriteText($"Welder Position: ({welderX}, {welderY}, {welderZ:f2})\n", true);
            screen.WriteText($"Accuracy to Pos: {CalculateAccuracy(welderX, welderY, welderZ):P2}\n", true);
            screen.WriteText($"Welder Target:   ({welderTargetX}, {welderTargetY}, {welderTargetZ:f2})\n", true);
            screen.WriteText($"Accuracy to Target: {CalculateAccuracy(welderTargetX, welderTargetY, welderTargetZ):P2}\n", true);
            screen.WriteText($"Task Queue: {taskQueue.Count} tasks\n", true);
            if (taskTimeLeft > 0)
            {
                screen.WriteText($"Current Task: {currentTask.description}\n", true);
                screen.WriteText($"Time Left: {taskTimeLeft:f3} s\n", true);
            }
            else if (heightScanInProgress)
            {
                screen.WriteText("Height scan in progress:\n", true);
                screen.WriteText($"Sensor Range: {sensorRange:f2} m\n", true);
                screen.WriteText($"Sensor Readings: {sensorReadings[Side.Top]:f2}, {sensorReadings[Side.Bottom]:f2}, {sensorReadings[Side.Left]:f2}, {sensorReadings[Side.Right]:f2}\n", true);
            }
            else
            {
                screen.WriteText("System idle\n", true);
            }
        }

        void Prints()
        {
            DebugScreen(debugScreen);
            DebugScreen(pbScreen);
            PrintMap();
        }

        void UpdateStatusLights()
        {
            foreach (IMyLightingBlock light in statusLights)
            {
                if (safetyLock)
                {
                    light.Color = new Color(255, 0, 0); // Red for safety lock engaged
                }
                else if (taskTimeLeft > 0)
                {
                    light.Color = new Color(255, 255, 0); // Yellow for active movement
                }
                else
                {
                    light.Color = new Color(0, 255, 0); // Green for idle and safe
                }
            }
        }

        void InitTasks()
        {
            if (!NeedsInitialization())
            {
                Echo("Pistons are already initialized.");
                return;
            }
            QueueEqualizeAllPistons(1f);
            safetyLock = false;
            QueueMove((int)Math.Round(welderX), (int)Math.Round(welderY), welderZ, 1f,
                description: "Final move to current welder position after piston equalization",
                onFinish: () => {
                        safetyLock = true;
                        if (!NeedsInitialization())
                        {
                            Echo("Pistons successfully initialized.");
                        }
                        else
                        {
                            Echo("Piston initialization failed: Pistons are still misaligned!");
                        }
                    });
        }

        bool NeedsInitialization()
        {
            foreach (Corner corner in corners)
            {
                float averageExtension = 0f;
                foreach (IMyPistonBase piston in horizontalPistonLists[corner])
                {
                    averageExtension += piston.CurrentPosition;
                }
                averageExtension /= horizontalPistonLists[corner].Count;
                foreach (IMyPistonBase piston in horizontalPistonLists[corner])
                {
                    if (Math.Abs(piston.CurrentPosition - averageExtension) > 0.01f)
                    {
                        return true;
                    }
                }

                float averageHeight = 0f;
                foreach (IMyPistonBase piston in verticalPistonLists[corner])
                {
                    averageHeight += piston.CurrentPosition;
                }
                averageHeight /= verticalPistonLists[corner].Count;
                foreach (IMyPistonBase piston in verticalPistonLists[corner])
                {
                    if (Math.Abs(piston.CurrentPosition - averageHeight) > 0.01f)
                    {
                        return true;
                    }
                }
            }
            return CalculateAccuracy(welderX, welderY, welderZ) < 0.99f;
        }

        void Update()
        {
            if (taskTimeLeft > 0)
            {
                taskTimeLeft -= (float)Runtime.TimeSinceLastRun.TotalSeconds;
                if (taskTimeLeft < 0)
                {
                    taskTimeLeft = 0f;
                    currentTask.finish();
                }
                return;
            }
            else if (taskQueue.Count > 0)
            {
                currentTask = taskQueue.Dequeue();
                taskTimeLeft = currentTask.execute();
                if (taskTimeLeft == 0f)
                {
                    currentTask.finish();
                }
            }
            else if (heightScanInProgress)
            {
                HeightScanStep();
            }
            UpdateStatusLights();
        }

        public void Main(string argument, UpdateType updateSource)
        {
            debugScreen.WriteText("", false);
            mapScreen.WriteText("", false);
            pbScreen.WriteText("", false);

            if (!string.IsNullOrEmpty(argument))
            {
                ParseCommand(argument);
            }

            Update();

            Prints();

        }
    }
}
