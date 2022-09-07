﻿using RimWorld;
using SmashTools;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Vehicles;
using Verse;
using Verse.AI;
using Verse.Sound;

namespace VanillaVehiclesExpanded
{
    public class CompProperties_VehicleMovementController : VehicleCompProperties
    {
        public CompProperties_VehicleMovementController()
        {
            this.compClass = typeof(CompVehicleMovementController);
        }
    }

    public enum MovementMode { Starting, Accelerate, CurrentSpeed, Decelerate}

    public struct StartAndDestCells
    {
        public IntVec3 start;
        public IntVec3 dest;
        public override bool Equals(object obj)
        {
            if (obj is StartAndDestCells other)
            {
                return this == other;
            }
            return false;
        }
        public static bool operator ==(StartAndDestCells a, StartAndDestCells b)
        {
            if (a.start == b.start && a.dest == b.dest)
            {
                return true;
            }
            return false;
        }

        public static bool operator !=(StartAndDestCells a, StartAndDestCells b)
        {
            if (a.start != b.start || a.dest != b.dest)
            {
                return true;
            }
            return false;
        }

        public override int GetHashCode()
        {
            return (start + dest).GetHashCode();
        }
    }

    [HotSwappable]
    public class CompVehicleMovementController : VehicleComp
    {
        public VehiclePawn Vehicle => this.parent as VehiclePawn;
        public float currentSpeed;
        public bool wasMoving;
        public MovementMode curMovementMode;
        public float curPaidPathCost;
        public bool isScreeching;
        private Sustainer sustainer;
        private Dictionary<StartAndDestCells, PawnPath> savedPaths = new Dictionary<StartAndDestCells, PawnPath>();
        public float AccelerationRate => Vehicle.GetStatValue(VVE_DefOf.AccelerationRate);
        public void StartMove()
        {
            if (wasMoving is false)
            {
                currentSpeed = 0;
            }
            curMovementMode = MovementMode.Accelerate;
            curPaidPathCost = 0;
            isScreeching = false;
            savedPaths.Clear();
        }

        public override void CompTick()
        {
            base.CompTick();
            wasMoving = Vehicle.vPather.Moving;
            if (wasMoving && Vehicle.vPather.curPath != null)
            {
                var moveSpeed = GetDefaultMoveSpeed();
                var totalCost = GetTotalCost();
                var decelerateInPctOfPath = (moveSpeed / (AccelerationRate * 2f)) / totalCost;
                var pctOfPathPassed = (curPaidPathCost / totalCost);
                if (decelerateInPctOfPath > 1f)
                {
                    if (pctOfPathPassed <= 0.5f && currentSpeed < moveSpeed && curMovementMode != MovementMode.Decelerate)
                    {
                        SpeedUp(moveSpeed, decelerateInPctOfPath, pctOfPathPassed);
                    }
                    else
                    {
                        Slowdown(decelerateInPctOfPath, pctOfPathPassed);
                    }
                }
                else
                {
                    if (pctOfPathPassed <= 1f - decelerateInPctOfPath && curMovementMode != MovementMode.Decelerate)
                    {
                        SpeedUp(moveSpeed, decelerateInPctOfPath, pctOfPathPassed);
                    }
                    else
                    {
                        Slowdown(decelerateInPctOfPath, pctOfPathPassed);
                    }
                }

                if (isScreeching)
                {
                    if (sustainer == null || sustainer.Ended)
                    {
                        sustainer = VVE_DefOf.VVE_TiresScreech.TrySpawnSustainer(SoundInfo.InMap(parent));
                    }
                    sustainer.Maintain();
                }
            }
            Log.ResetMessageCount();
        }

        private void Slowdown(float deceleratePct, float pctPassed)
        {
            curMovementMode = MovementMode.Decelerate;
            var newSpeed = currentSpeed - AccelerationRate;
            var remainingArrivalTicks = GetTicksToDestination();
            var slowdownMultiplier = (currentSpeed / (AccelerationRate * 2f)) / remainingArrivalTicks;
            if (slowdownMultiplier >= 2f && deceleratePct > 1f)
            {
                newSpeed /= slowdownMultiplier;
                isScreeching = true;
                Messages.Message("VVE_HandbrakeWarning".Translate(Vehicle.Named("VEHICLE")), MessageTypeDefOf.NegativeHealthEvent);
                var damageAmount = Mathf.CeilToInt(slowdownMultiplier);
                Log.Message("Damage: " + damageAmount);
                var log = "slowdownMultiplier: " + slowdownMultiplier + " - (currentSpeed / AccelerationRate): " + (currentSpeed / AccelerationRate);
                //LogMode("Handbrake: " + log, pctPassed, deceleratePct);
                Vehicle.Map.debugDrawer.FlashCell(Vehicle.Position, 0.1f, duration: 10000);
                //VehicleComponent component = Vehicle.statHandler.components.Where(component => !component.props.categories.Contains(VehicleStatDefOf.BodyIntegrity)).RandomOrDefault();
            }
            else
            {
                //LogMode("Deceleration: ", pctPassed, deceleratePct);
                Vehicle.Map.debugDrawer.FlashCell(Vehicle.Position, 0.1f, duration: 10000);
            }
            newSpeed = Mathf.Max(1f, newSpeed);
            if (currentSpeed > newSpeed)
            {
                currentSpeed = newSpeed;
            }
        }

        private void SpeedUp(float moveSpeed, float deceleratePct, float pctPassed)
        {
            if (moveSpeed > currentSpeed)
            {
                curMovementMode = MovementMode.Accelerate;
                currentSpeed = Mathf.Min(currentSpeed + AccelerationRate, moveSpeed);
                //LogMode("Acceleration", pctPassed, deceleratePct);
                Vehicle.Map.debugDrawer.FlashCell(Vehicle.Position, 0.5f, duration: 10000);
            }
            else
            {
                //LogMode("Cur speed", pctPassed, deceleratePct);
                Vehicle.Map.debugDrawer.FlashCell(Vehicle.Position, 0.7f, duration: 10000);
                curMovementMode = MovementMode.CurrentSpeed;
            }
        }

        private void LogMode(string logMode, float pctPassed, float deceleratePct)
        {
            Log.Message(logMode + ": currentSpeed: " + currentSpeed
                + " - curPaidPathCost: " + curPaidPathCost + " - pctOfPathPassed: "
                + pctPassed + " - decelerateInPctOfPath: " + deceleratePct + " - remaining ticks: " + GetTicksToDestination() +
                " - TotalCost: " + GetTotalCost());
        }

        private float GetTicksToDestination()
        {
            var cost = 0f;
            var path = Vehicle.vPather.curPath;
            cost += GetPathCostIgnorePassedCells(path);
            var pos = path.LastNode;
            foreach (var queueJob in Vehicle.jobs.jobQueue)
            {
                if (queueJob.job.def == JobDefOf.Goto)
                {
                    path = GetPawnPath(pos, queueJob.job.targetA.Cell);
                    if (path != null)
                    {
                        pos = path.LastNode;
                        cost += GetPathCostIgnorePassedCells(path);
                    }
                }
            }
            return cost;
        }
        private float GetTotalCost()
        {
            var path = Vehicle.vPather.curPath;
            var cost = path.TotalCost;
            var pos = path.LastNode;
            foreach (var queueJob in Vehicle.jobs.jobQueue)
            {
                if (queueJob.job.def == JobDefOf.Goto)
                {
                    path = GetPawnPath(pos, queueJob.job.targetA.Cell);
                    if (path != null)
                    {
                        pos = path.LastNode;
                        cost += path.TotalCost;
                    }
                }
                else
                {
                    break;
                }
            }
            return cost;
        }

        private PawnPath GetPawnPath(IntVec3 start, IntVec3 dest)
        {
            var key = new StartAndDestCells { start = start, dest = dest };
            if (!savedPaths.TryGetValue(key, out var path))
            {
                savedPaths[key] = path = Vehicle.Map.pathFinder.FindPath(start, dest, Vehicle, PathEndMode.OnCell);
                Log.Message("Saved path: " + path);
            }
            else
            {
                Log.Message("Found path: " + path);
            }
            return path;
        }

        public float GetPathCostIgnorePassedCells(PawnPath path)
        {
            var cost = 0f;
            if (path != null)
            {
                var prevCell = Vehicle.Position;
                bool startCalculation = false;
                var nodes = path.NodesReversed.ListFullCopy();
                nodes.Reverse();
                foreach (var cell in nodes)
                {
                    if (startCalculation)
                    {
                        cost += CostToMoveIntoCell(Vehicle, prevCell, cell);
                    }
                    if (cell == Vehicle.Position)
                    {
                        startCalculation = true;
                    }
                    prevCell = cell;
                }
            }
            return cost;
        }
        private static int CostToMoveIntoCell(VehiclePawn vehicle, IntVec3 prevCell, IntVec3 c)
        {
            int num;
            VehicleStatPart_AccelerationRate.modifyValue = true;
            if (c.x == prevCell.x || c.z == prevCell.z)
            {
                num = vehicle.TicksPerMoveCardinal;
            }
            else
            {
                num = vehicle.TicksPerMoveDiagonal;
            }
            VehicleStatPart_AccelerationRate.modifyValue = false;

            num += vehicle.Map.GetCachedMapComponent<VehicleMapping>()[vehicle.VehicleDef].VehiclePathGrid.CalculatedCostAt(c);
            Building edifice = c.GetEdifice(vehicle.Map);
            if (edifice != null)
            {
                num += edifice.PathWalkCostFor(vehicle);
            }
            if (num > Vehicle_PathFollower.MaxMoveTicks)
            {
                num = Vehicle_PathFollower.MaxMoveTicks;
            }
            if (vehicle.CurJob != null)
            {
                Pawn locomotionUrgencySameAs = vehicle.jobs.curDriver.locomotionUrgencySameAs;
                if (locomotionUrgencySameAs is VehiclePawn locomotionVehicle && locomotionUrgencySameAs != vehicle && locomotionUrgencySameAs.Spawned)
                {
                    int num2 = CostToMoveIntoCell(locomotionVehicle, prevCell, c);
                    if (num < num2)
                    {
                        num = num2;
                    }
                }
                else
                {
                    switch (vehicle.jobs.curJob.locomotionUrgency)
                    {
                        case LocomotionUrgency.Amble:
                            num *= 3;
                            if (num < Vehicle_PathFollower.MinCostAmble)
                            {
                                num = Vehicle_PathFollower.MinCostAmble;
                            }
                            break;
                        case LocomotionUrgency.Walk:
                            num *= 2;
                            if (num < Vehicle_PathFollower.MinCostWalk)
                            {
                                num = Vehicle_PathFollower.MinCostWalk;
                            }
                            break;
                        case LocomotionUrgency.Jog:
                            break;
                        case LocomotionUrgency.Sprint:
                            num = Mathf.RoundToInt(num * 0.75f);
                            break;
                    }
                }
            }
            return Mathf.Max(num, 1);
        }
        public float GetDefaultMoveSpeed()
        {
            return Vehicle.GetStatValue(VehicleStatDefOf.MoveSpeed);
        }
        public override void PostExposeData()
        {
            base.PostExposeData();
            Scribe_Values.Look(ref curPaidPathCost, "curPaidPathCost");
            Scribe_Values.Look(ref wasMoving, "wasMoving");
            Scribe_Values.Look(ref currentSpeed, "currentSpeed");
            Scribe_Values.Look(ref curMovementMode, "curMovementMode");
            Scribe_Values.Look(ref isScreeching, "isScreeching");
        }
    }
}
