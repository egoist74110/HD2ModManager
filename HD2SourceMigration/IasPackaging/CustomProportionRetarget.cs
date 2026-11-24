using HD2SourceMigration;
using HD2SourceMigration.Retarget;
using HD2SourceModel;

namespace HD2SourceMigration.IasPackaging;

internal sealed record CustomHandDiagnostics(
	string Side,
	double ShoulderRotateDeg,
	double ElbowRotateDeg,
	double ForearmTwistDeg,
	double WristSwingDeg,
	double PalmFrameResidualDeg,
	double MaxFingerResidualDeg,
	double MeanFingerMeshToBoneMm);

internal sealed record CustomProportionSolution(
	RetargetResult Retarget,
	Dictionary<int, Mat4> CustomWorldByIndex,
	IReadOnlyList<CustomHandDiagnostics> HandDiagnostics,
	IReadOnlyDictionary<string, Vec3> JiggleTipWorldByBoneName);

/// <summary>
/// 保留 VPK 的肩宽和四肢比例，使用原版 HD2 骨骼的静止旋转，让 IAS 沿原版轴驱动。
/// 以刚体变换对齐手臂，将掌部旋转分配给前臂扭转和手腕摆动，再把手指关节对齐原版方向。
/// </summary>
internal static class CustomProportionRetarget
{
	private static readonly (string Stem, string SourceSuffix)[] FingerStems =
	[
		("thumb", "Finger0"),
		("index", "Finger1"),
		("middle", "Finger2"),
		("ring", "Finger3"),
		("pinky", "Finger4"),
	];

	public static CustomProportionSolution Solve(RetargetInputs inputs, Hd2Rig rig)
	{
		var unposedOptions = new RetargetOptions
		{
			Alignment = RetargetAlignment.Yaw,
			SpineMap = RetargetSpineMap.Mesh,
			HeadOffset = -0.015,
			LegTilt = 5.7,
			LegSplay = 4.0,
			Skeleton = RetargetSkeleton.Snap,
			AxisDeltas = false,
			AxisRoundtrip = false,
			IncludeJiggleBones = true,
			MeshShift = Vec3.Zero,
			GroundLift = 0.0,
			UpperScale = 1.0,
			HeadScale = 1.0,
			ArmScratch = false,
			ArmFit = 0.0,
			FingerPose = false,
			HandReorient = HandReorientMode.Off,
			PalmScale = 1.0,
			PalmScaleAuto = false,
		};

		var unposed = new Retargeter(
			inputs.Model,
			inputs.Geometry,
			rig,
			BodyPoseProfile.Parse("{}"),
			unposedOptions).Run();

		var byName = rig.Bones
			.Where(b => b.HasName)
			.ToDictionary(b => b.Name!, b => b, StringComparer.Ordinal);

		var boneCount = rig.Bones.Count;
		var restHd2 = new Mat4[boneCount];
		var customWorld = new Dictionary<int, Mat4>(boneCount);
		var children = new List<int>?[boneCount];

		foreach (var b in rig.Bones)
		{
			restHd2[b.Index] = b.WorldBind;
			var origin = unposed.WorkingRest.TryGetValue(b.Index, out var wr)
				? wr.Origin
				: b.WorldBind.Origin;
			customWorld[b.Index] = MakeWorld(Mat3.FromMat4(b.WorldBind), origin);

			if (b.ParentIndex >= 0 && b.ParentIndex != b.Index && b.ParentIndex < boneCount)
				(children[b.ParentIndex] ??= []).Add(b.Index);
		}

		// 绕自定义肩和肘刚性旋转上臂与前臂，保留骨长。
		var armDeform = new Dictionary<int, Mat4>(boneCount);
		for (var i = 0; i < boneCount; i++)
			armDeform[i] = Mat4.Identity;

		var sideArmMeta = new Dictionary<string, (
			Vec3 S,
			Vec3 ECustom,
			Vec3 WCustom,
			Vec3 UFore,
			double LFore,
			Mat4 DSh,
			Mat4 DEl,
			double ShDeg,
			double ElDeg)>(StringComparer.Ordinal);

		foreach (var side in new[] { "l", "r" })
		{
			var shIdx = byName[$"{side}_shoulder"].Index;
			var elIdx = byName[$"{side}_elbow"].Index;
			var haIdx = byName[$"{side}_hand"].Index;

			var sPos = unposed.WorkingRest[shIdx].Origin;
			var e0 = unposed.WorkingRest[elIdx].Origin;
			var w0 = unposed.WorkingRest[haIdx].Origin;

			var lUpper = (e0 - sPos).Length;
			var lFore = (w0 - e0).Length;

			var uUpper = RetargetMath.SafeNormalize(restHd2[elIdx].Origin - restHd2[shIdx].Origin);
			var uFore = RetargetMath.SafeNormalize(restHd2[haIdx].Origin - restHd2[elIdx].Origin);

			var eCustom = sPos + (uUpper * lUpper);
			var wCustom = eCustom + (uFore * lFore);

			var rSh = RetargetMath.MinArcRotation(e0 - sPos, uUpper);
			var dSh = AroundPoint(sPos, rSh);

			var vForeAfterSh = rSh.Transform(w0 - e0);
			var rEl = RetargetMath.MinArcRotation(vForeAfterSh, uFore);
			var dEl = AroundPoint(eCustom, rEl) * dSh;

			// 先为肩部子树应用 D_sh，再用 D_el 覆盖肘部子树。
			foreach (var sub in Subtree(children, shIdx))
				armDeform[sub] = dSh;
			foreach (var sub in Subtree(children, elIdx))
				armDeform[sub] = dEl;

			customWorld[shIdx] = MakeWorld(Mat3.FromMat4(restHd2[shIdx]), sPos);
			customWorld[elIdx] = MakeWorld(Mat3.FromMat4(restHd2[elIdx]), eCustom);
			customWorld[haIdx] = MakeWorld(Mat3.FromMat4(restHd2[haIdx]), wCustom);

			if (byName.TryGetValue($"{side}_shoulder_twist", out var shTwist))
				customWorld[shTwist.Index] = MakeWorld(Mat3.FromMat4(restHd2[shTwist.Index]), sPos + ((eCustom - sPos) * 0.42));
			if (byName.TryGetValue($"{side}_hand_twist", out var haTwist))
				customWorld[haTwist.Index] = MakeWorld(Mat3.FromMat4(restHd2[haTwist.Index]), wCustom);

			sideArmMeta[side] = (
				sPos,
				eCustom,
				wCustom,
				uFore,
				lFore,
				dSh,
				dEl,
				RetargetMath.AngleAxis(rSh).Degrees,
				RetargetMath.AngleAxis(rEl).Degrees);
		}

		// 用刚性手臂变换更新网格蒙皮。
		var pts = SkinMesh(inputs.Geometry, unposed.Output, unposed.Resolved, armDeform);

		// 分解掌部的扭转和摆动，再求解 30 节手指骨的 FK。
		var fingerRows = new List<FingerPoseRow>();
		var handRows = new List<HandReorientRow>();
		var handDiags = new List<CustomHandDiagnostics>();
		var fullPointTransformsByBone = new Dictionary<int, Func<Vec3, Vec3>>();
		for (var i = 0; i < boneCount; i++)
		{
			var d = armDeform[i];
			fullPointTransformsByBone[i] = p => d.TransformPoint(p);
		}

		foreach (var side in new[] { "l", "r" })
		{
			var meta = sideArmMeta[side];
			var haIdx = byName[$"{side}_hand"].Index;
			var elIdx = byName[$"{side}_elbow"].Index;

			// 比较应用 D_el 后的掌部坐标系和原版 HD2 掌部坐标系。
			var fHd2 = ComputePalmFrame(byName, side, idx => restHd2[idx].Origin);
			var fEl = ComputePalmFrame(byName, side, idx => meta.DEl.TransformPoint(unposed.WorkingRest[idx].Origin));
			var rPalm = fHd2 * fEl.Transposed();

			var (rTwist, twistRad, rSwing, swingAxis, swingRad) = DecomposeSwingTwist(rPalm, meta.UFore);

			var distalSet = BuildForearmAndHandSet(byName, side);
			var handOnlySet = BuildHandAndFingerSet(byName, side);

			// 沿肘到腕方向平滑分配前臂扭转，并在腕部衰减摆动。
			for (var v = 0; v < pts.Length; v++)
			{
				var (wForeDistal, wHand) = GetGroupWeights(inputs.Geometry.Vertices[v], unposed.Resolved, distalSet, handOnlySet);
				if (wForeDistal <= 1e-6)
					continue;

				var p = pts[v];
				var seg = p - meta.ECustom;
				var along = Vec3.Dot(seg, meta.UFore);
				var proj = RetargetMath.Clamp(along / Math.Max(meta.LFore, 1e-9), 0.0, 1.0);
				var perp = (seg - (meta.UFore * (proj * meta.LFore))).Length;

				if (wHand < 0.20 && perp >= RetargetConstants.ForearmGateRadius)
					continue;

				var sAxial = wHand >= 0.50 ? 1.0 : proj;
				var twistFrac = wForeDistal * Smoothstep(sAxial);
				if (Math.Abs(twistRad * twistFrac) > 1e-9)
				{
					var rotT = RetargetMath.RotAxisAngle(meta.UFore, twistRad * twistFrac);
					p = meta.ECustom + rotT.Transform(p - meta.ECustom);
				}

				if (wHand > 1e-6 && Math.Abs(swingRad) > 1e-9)
				{
					var wristAxial = Vec3.Dot(p - meta.WCustom, meta.UFore);
					var wristRamp = wHand >= 0.85 && IsPureFingerVertex(inputs.Geometry.Vertices[v], unposed.Resolved, byName, side)
						? 1.0
						: Smoothstep(RetargetMath.Clamp((wristAxial + 0.025) / 0.050, 0.0, 1.0));
					var swingFrac = wHand * wristRamp;
					if (Math.Abs(swingRad * swingFrac) > 1e-9)
					{
						var rotS = RetargetMath.RotAxisAngle(swingAxis, swingRad * swingFrac);
						p = meta.WCustom + rotS.Transform(p - meta.WCustom);
					}
				}

				pts[v] = p;
			}

			// 更新锚在前臂或手掌上的摆动骨的点变换。
			var dElCopy = meta.DEl;
			var eC = meta.ECustom;
			var wC = meta.WCustom;
			var uF = meta.UFore;
			var lF = meta.LFore;
			fullPointTransformsByBone[elIdx] = p0 =>
			{
				var p1 = dElCopy.TransformPoint(p0);
				var sAx = RetargetMath.Clamp(Vec3.Dot(p1 - eC, uF) / Math.Max(lF, 1e-9), 0.0, 1.0);
				return eC + RetargetMath.RotAxisAngle(uF, twistRad * Smoothstep(sAx)).Transform(p1 - eC);
			};
			foreach (var hb in handOnlySet)
			{
				fullPointTransformsByBone[hb] = p0 =>
				{
					var p1 = dElCopy.TransformPoint(p0);
					return wC + rPalm.Transform(p1 - wC);
				};
			}

			// 将这一侧的五指（15 个关节）从掌部对齐姿态转向原版 HD2 手指方向。
			var fingerFrames = new Dictionary<int, Mat4>();
			var maxFingerResDeg = 0.0;

			foreach (var (stem, srcSuffix) in FingerStems)
			{
				var hd2Names = new[] { 1, 2, 3 }.Select(k => $"{side}_{stem}_finger{k}").ToArray();
				var srcNames = new[] { string.Empty, "1", "2" }
					.Select(sfx => $"{side.ToUpperInvariant()}_{srcSuffix}{sfx}").ToArray();
				if (hd2Names.Any(n => !byName.ContainsKey(n)))
					continue;

				var indices = hd2Names.Select(n => byName[n].Index).ToArray();
				var joints = new Vec3[3];
				var working = new Vec3[3];
				for (var k = 0; k < 3; k++)
				{
					working[k] = unposed.WorkingRest[indices[k]].Origin;
					var afterEl = meta.DEl.TransformPoint(working[k]);
					joints[k] = meta.WCustom + rPalm.Transform(afterEl - meta.WCustom);
				}

				var sourceBend = RetargetMath.SafeAcos(
					Vec3.Dot(
						RetargetMath.SafeNormalize(working[1] - working[0]),
						RetargetMath.SafeNormalize(working[2] - working[1]))) * (180.0 / Math.PI);

				var segments = new[]
				{
					RetargetMath.SafeNormalize(joints[1] - joints[0]),
					RetargetMath.SafeNormalize(joints[2] - joints[1]),
					RetargetMath.SafeNormalize(joints[2] - joints[1]),
				};

				var targets = new[]
				{
					RetargetMath.SafeNormalize(restHd2[indices[1]].Origin - restHd2[indices[0]].Origin),
					RetargetMath.SafeNormalize(restHd2[indices[2]].Origin - restHd2[indices[1]].Origin),
					RetargetMath.SafeNormalize(new Vec3(
						restHd2[indices[2]].M11,
						restHd2[indices[2]].M21,
						restHd2[indices[2]].M31)),
				};

				var chain = Mat4.Identity;
				var angles = new double[3];
				var residuals = new double[3];

				for (var k = 0; k < 3; k++)
				{
					var q = Mat3.FromMat4(chain);
					var local = RetargetMath.MinArcRotation(segments[k], q.Transposed().Transform(targets[k]));
					angles[k] = RetargetMath.AngleAxis(local).Degrees;
					chain = chain * AroundPoint(joints[k], local);
					fingerFrames[indices[k]] = chain;
				}

				for (var k = 0; k < 3; k++)
				{
					var got = Mat3.FromMat4(fingerFrames[indices[k]]).Transform(segments[k]);
					residuals[k] = RetargetMath.SafeAcos(
						Vec3.Dot(RetargetMath.SafeNormalize(got), targets[k])) * (180.0 / Math.PI);
					maxFingerResDeg = Math.Max(maxFingerResDeg, residuals[k]);
				}

				// 自定义指骨头位于摆姿后的关节中心，骨轴沿用原版 HD2。
				var posedJ0 = joints[0];
				var posedJ1 = fingerFrames[indices[0]].TransformPoint(joints[1]);
				var posedJ2 = fingerFrames[indices[1]].TransformPoint(joints[2]);
				customWorld[indices[0]] = MakeWorld(Mat3.FromMat4(restHd2[indices[0]]), posedJ0);
				customWorld[indices[1]] = MakeWorld(Mat3.FromMat4(restHd2[indices[1]]), posedJ1);
				customWorld[indices[2]] = MakeWorld(Mat3.FromMat4(restHd2[indices[2]]), posedJ2);

				for (var k = 0; k < 3; k++)
				{
					var ff = fingerFrames[indices[k]];
					fullPointTransformsByBone[indices[k]] = p0 =>
					{
						var p1 = dElCopy.TransformPoint(p0);
						var p2 = wC + rPalm.Transform(p1 - wC);
						return ff.TransformPoint(p2);
					};
				}

				fingerRows.Add(new FingerPoseRow(side, stem, hd2Names, srcNames, angles, residuals, sourceBend));
			}

			pts = SkinMeshSubset(inputs.Geometry, pts, unposed.Resolved, fingerFrames);

			// 把姿态传给手部挂点等非指骨子节点，之后再求解握持骨。
			foreach (var childIdx in Subtree(children, haIdx))
			{
				if (childIdx == haIdx || IsFingerIndex(byName, side, childIdx))
					continue;
				var parentIdx = rig.Bones[childIdx].ParentIndex;
				if (parentIdx >= 0)
				{
					var stockRel = restHd2[parentIdx].Inverse() * restHd2[childIdx];
					var placed = customWorld[parentIdx] * stockRel;
					customWorld[childIdx] = MakeWorld(Mat3.FromMat4(restHd2[childIdx]), placed.Origin);
				}
			}

			var fAfter = ComputePalmFrame(byName, side, idx => customWorld[idx].Origin);
			var palmResDeg = RetargetMath.AngleAxis(fHd2 * fAfter.Transposed()).Degrees;
			var meanFingerDistM = MeasureFingerMeshToBoneDistance(inputs.Geometry, pts, unposed.Resolved, byName, customWorld, side);

			handRows.Add(new HandReorientRow(
				side,
				swingRad * 180.0 / Math.PI,
				null,
				meanFingerDistM,
				CountHandVertices(inputs.Geometry, unposed.Resolved, handOnlySet)));

			handDiags.Add(new CustomHandDiagnostics(
				Side: side,
				ShoulderRotateDeg: Math.Round(meta.ShDeg, 3),
				ElbowRotateDeg: Math.Round(meta.ElDeg, 3),
				ForearmTwistDeg: Math.Round(twistRad * 180.0 / Math.PI, 3),
				WristSwingDeg: Math.Round(swingRad * 180.0 / Math.PI, 3),
				PalmFrameResidualDeg: Math.Round(palmResDeg, 4),
				MaxFingerResidualDeg: Math.Round(maxFingerResDeg, 4),
				MeanFingerMeshToBoneMm: Math.Round(meanFingerDistM * 1000.0, 2)));
		}

		// 更新摆动骨姿态，计算 IAS .hd2phys 所需的末端位置。
		var posedExtras = new List<RetargetExtraBone>(unposed.ExtraBones.Count);
		var extraOriginByName = new Dictionary<string, Vec3>(StringComparer.Ordinal);
		foreach (var extra in unposed.ExtraBones)
		{
			var posedOrigin = extra.AnchorIndex >= 0 && fullPointTransformsByBone.TryGetValue(extra.AnchorIndex, out var fn)
				? fn(extra.WorldOrigin)
				: extra.WorldOrigin;
			posedExtras.Add(extra with { WorldOrigin = posedOrigin });
			extraOriginByName[extra.Name] = posedOrigin;
		}

		var tipWorldByName = ComputeJiggleTipWorlds(
			inputs.Model,
			unposed,
			posedExtras,
			extraOriginByName,
			customWorld);

		var updatedMetrics = unposed.Metrics with
		{
			HeightAfter = pts.Max(p => p.Z) - pts.Min(p => p.Z),
			SoleZ = pts.Min(p => p.Z),
		};

		var finalResult = new RetargetResult(
			SourcePoints: unposed.SourcePoints,
			Aligned: unposed.Aligned,
			Output: pts,
			IdleOutput: pts,
			WorkingRest: unposed.WorkingRest,
			ShippedRest: customWorld,
			Deform: armDeform,
			Resolved: unposed.Resolved,
			Hops: unposed.Hops,
			Fingers: fingerRows,
			HandReorient: handRows,
			PalmGirth: unposed.PalmGirth,
			ExtraBones: posedExtras,
			JiggleWeightTargets: unposed.JiggleWeightTargets,
			JiggleAnchors: unposed.JiggleAnchors,
			Metrics: updatedMetrics);

		return new CustomProportionSolution(finalResult, customWorld, handDiags, tipWorldByName);
	}

	private static Dictionary<string, Vec3> ComputeJiggleTipWorlds(
		SourceModelDocument model,
		RetargetResult unposed,
		IReadOnlyList<RetargetExtraBone> posedExtras,
		IReadOnlyDictionary<string, Vec3> extraOriginByName,
		IReadOnlyDictionary<int, Mat4> customWorld)
	{
		var srcBoneByName = model.Bones.ToDictionary(b => b.DisplayName, b => b, StringComparer.Ordinal);
		var scale = unposed.Metrics.Scale;
		var tips = new Dictionary<string, Vec3>(StringComparer.Ordinal);

		foreach (var extra in posedExtras)
		{
			var head = extra.WorldOrigin;
			Vec3 parentPos;
			if (extra.ParentIndex >= 0 && extra.ParentIndex < customWorld.Count && customWorld.TryGetValue(extra.ParentIndex, out var pw))
			{
				parentPos = pw.Origin;
			}
			else
			{
				var parentExtraIdx = extra.ParentIndex - customWorld.Count;
				parentPos = parentExtraIdx >= 0 && parentExtraIdx < posedExtras.Count
					? posedExtras[parentExtraIdx].WorldOrigin
					: head - new Vec3(0, 0, -0.05);
			}

			var rawLen = 0.05;
			if (srcBoneByName.TryGetValue(extra.Name, out var sb) && sb.Jiggle is not null)
			{
				rawLen = Math.Clamp(sb.Jiggle.Length * RetargetConstants.InchesToMetres * scale, 0.02, 0.22);
			}

			var dir = head - parentPos;
			dir = dir.Length > 1e-4
				? dir.Normalized()
				: new Vec3(0, 0, -1);

			tips[extra.Name] = head + (dir * rawLen);
		}

		return tips;
	}

	private static Mat3 ComputePalmFrame(
		IReadOnlyDictionary<string, Hd2RigBone> byName,
		string side,
		Func<int, Vec3> originOf)
	{
		var origin = originOf(byName[$"{side}_hand"].Index);
		var lm = new[]
		{
			originOf(byName[$"{side}_index_finger1"].Index),
			originOf(byName[$"{side}_middle_finger1"].Index),
			originOf(byName[$"{side}_ring_finger1"].Index),
			originOf(byName[$"{side}_pinky_finger1"].Index),
		};
		var center = (lm[0] + lm[1] + lm[2] + lm[3]) * 0.25;
		var forward = (center - origin).Normalized();
		var width = lm[0] - lm[3];
		width = (width - (forward * Vec3.Dot(width, forward))).Normalized();
		var normal = Vec3.Cross(forward, width).Normalized();
		width = Vec3.Cross(normal, forward).Normalized();

		return new Mat3(
			forward.X, width.X, normal.X,
			forward.Y, width.Y, normal.Y,
			forward.Z, width.Z, normal.Z);
	}

	private static (Mat3 RTwist, double TwistRad, Mat3 RSwing, Vec3 SwingAxis, double SwingRad) DecomposeSwingTwist(
		Mat3 rot,
		Vec3 unitAxis)
	{
		var (w, x, y, z) = Mat3ToQuatWxyz(rot);
		if (w < 0)
		{
			w = -w;
			x = -x;
			y = -y;
			z = -z;
		}

		var v = new Vec3(x, y, z);
		var proj = Vec3.Dot(v, unitAxis);
		var twistRad = 2.0 * Math.Atan2(proj, w);
		var rTwist = RetargetMath.RotAxisAngle(unitAxis, twistRad);
		var rSwing = rot * rTwist.Transposed();
		var (swingDeg, swingAxis) = RetargetMath.AngleAxis(rSwing);
		return (rTwist, twistRad, rSwing, swingAxis, swingDeg * Math.PI / 180.0);
	}

	private static (double W, double X, double Y, double Z) Mat3ToQuatWxyz(Mat3 m)
	{
		var trace = m.M11 + m.M22 + m.M33;
		double w, x, y, z;
		if (trace > 0.0)
		{
			var s = Math.Sqrt(trace + 1.0) * 2.0;
			w = 0.25 * s;
			x = (m.M32 - m.M23) / s;
			y = (m.M13 - m.M31) / s;
			z = (m.M21 - m.M12) / s;
		}
		else if (m.M11 > m.M22 && m.M11 > m.M33)
		{
			var s = Math.Sqrt(1.0 + m.M11 - m.M22 - m.M33) * 2.0;
			w = (m.M32 - m.M23) / s;
			x = 0.25 * s;
			y = (m.M12 + m.M21) / s;
			z = (m.M13 + m.M31) / s;
		}
		else if (m.M22 > m.M33)
		{
			var s = Math.Sqrt(1.0 + m.M22 - m.M11 - m.M33) * 2.0;
			w = (m.M13 - m.M31) / s;
			x = (m.M12 + m.M21) / s;
			y = 0.25 * s;
			z = (m.M23 + m.M32) / s;
		}
		else
		{
			var s = Math.Sqrt(1.0 + m.M33 - m.M11 - m.M22) * 2.0;
			w = (m.M21 - m.M12) / s;
			x = (m.M13 + m.M31) / s;
			y = (m.M23 + m.M32) / s;
			z = 0.25 * s;
		}

		var len = Math.Sqrt((w * w) + (x * x) + (y * y) + (z * z));
		return len > 1e-12 ? (w / len, x / len, y / len, z / len) : (1.0, 0.0, 0.0, 0.0);
	}

	private static HashSet<int> BuildForearmAndHandSet(IReadOnlyDictionary<string, Hd2RigBone> byName, string side)
	{
		var set = BuildHandAndFingerSet(byName, side);
		if (byName.TryGetValue($"{side}_elbow", out var el))
			set.Add(el.Index);
		return set;
	}

	private static HashSet<int> BuildHandAndFingerSet(IReadOnlyDictionary<string, Hd2RigBone> byName, string side)
	{
		var set = new HashSet<int>();
		foreach (var n in new[] { $"{side}_hand", $"{side}_hand_twist" })
		{
			if (byName.TryGetValue(n, out var b))
				set.Add(b.Index);
		}
		foreach (var (stem, _) in FingerStems)
		{
			for (var k = 1; k <= 3; k++)
			{
				if (byName.TryGetValue($"{side}_{stem}_finger{k}", out var fb))
					set.Add(fb.Index);
			}
		}
		return set;
	}

	private static bool IsFingerIndex(IReadOnlyDictionary<string, Hd2RigBone> byName, string side, int idx)
	{
		foreach (var (stem, _) in FingerStems)
		{
			for (var k = 1; k <= 3; k++)
			{
				if (byName.TryGetValue($"{side}_{stem}_finger{k}", out var fb) && fb.Index == idx)
					return true;
			}
		}
		return false;
	}

	private static bool IsPureFingerVertex(
		SourceVertex v,
		IReadOnlyDictionary<int, int> resolved,
		IReadOnlyDictionary<string, Hd2RigBone> byName,
		string side)
	{
		var fingerW = 0.0;
		var totalW = 0.0;
		foreach (var inf in v.Influences())
		{
			totalW += inf.Weight;
			if (resolved.TryGetValue(inf.BoneIndex, out var hd2) && IsFingerIndex(byName, side, hd2))
				fingerW += inf.Weight;
		}
		return totalW > 1e-6 && (fingerW / totalW) >= 0.50;
	}

	private static (double WForeDistal, double WHand) GetGroupWeights(
		SourceVertex v,
		IReadOnlyDictionary<int, int> resolved,
		HashSet<int> foreSet,
		HashSet<int> handSet)
	{
		var wf = 0.0;
		var wh = 0.0;
		var total = 0.0;
		foreach (var inf in v.Influences())
		{
			total += inf.Weight;
			if (resolved.TryGetValue(inf.BoneIndex, out var hd2))
			{
				if (foreSet.Contains(hd2))
					wf += inf.Weight;
				if (handSet.Contains(hd2))
					wh += inf.Weight;
			}
		}
		return total > 1e-9 ? (wf / total, wh / total) : (0.0, 0.0);
	}

	private static Vec3[] SkinMesh(
		SourceGeometry geom,
		IReadOnlyList<Vec3> pts,
		IReadOnlyDictionary<int, int> resolved,
		IReadOnlyDictionary<int, Mat4> frames)
	{
		var res = new Vec3[pts.Count];
		for (var i = 0; i < pts.Count; i++)
		{
			var acc = Vec3.Zero;
			var tot = 0.0;
			foreach (var inf in geom.Vertices[i].Influences())
			{
				var m = resolved.TryGetValue(inf.BoneIndex, out var hd2) && frames.TryGetValue(hd2, out var f)
					? f
					: Mat4.Identity;
				acc += m.TransformPoint(pts[i]) * inf.Weight;
				tot += inf.Weight;
			}
			res[i] = tot > 0.0 ? acc / tot : pts[i];
		}
		return res;
	}

	private static Vec3[] SkinMeshSubset(
		SourceGeometry geom,
		IReadOnlyList<Vec3> pts,
		IReadOnlyDictionary<int, int> resolved,
		IReadOnlyDictionary<int, Mat4> subsetFrames)
	{
		var res = new Vec3[pts.Count];
		for (var i = 0; i < pts.Count; i++)
		{
			var touched = false;
			foreach (var inf in geom.Vertices[i].Influences())
			{
				if (resolved.TryGetValue(inf.BoneIndex, out var hd2) && subsetFrames.ContainsKey(hd2))
				{
					touched = true;
					break;
				}
			}
			if (!touched)
			{
				res[i] = pts[i];
				continue;
			}

			var acc = Vec3.Zero;
			var tot = 0.0;
			foreach (var inf in geom.Vertices[i].Influences())
			{
				var m = resolved.TryGetValue(inf.BoneIndex, out var hd2) && subsetFrames.TryGetValue(hd2, out var f)
					? f
					: Mat4.Identity;
				acc += m.TransformPoint(pts[i]) * inf.Weight;
				tot += inf.Weight;
			}
			res[i] = tot > 0.0 ? acc / tot : pts[i];
		}
		return res;
	}

	private static double MeasureFingerMeshToBoneDistance(
		SourceGeometry geom,
		IReadOnlyList<Vec3> pts,
		IReadOnlyDictionary<int, int> resolved,
		IReadOnlyDictionary<string, Hd2RigBone> byName,
		IReadOnlyDictionary<int, Mat4> customWorld,
		string side)
	{
		var segs = new List<(Vec3 A, Vec3 B)>();
		var fingerBoneIds = new HashSet<int>();
		foreach (var (stem, _) in FingerStems)
		{
			var n1 = $"{side}_{stem}_finger1";
			var n2 = $"{side}_{stem}_finger2";
			var n3 = $"{side}_{stem}_finger3";
			if (!byName.ContainsKey(n1) || !byName.ContainsKey(n2) || !byName.ContainsKey(n3))
				continue;
			var i1 = byName[n1].Index;
			var i2 = byName[n2].Index;
			var i3 = byName[n3].Index;
			fingerBoneIds.Add(i1);
			fingerBoneIds.Add(i2);
			fingerBoneIds.Add(i3);

			var p1 = customWorld[i1].Origin;
			var p2 = customWorld[i2].Origin;
			var p3 = customWorld[i3].Origin;
			var m3 = customWorld[i3];
			var ax3 = new Vec3(m3.M11, m3.M21, m3.M31).Normalized();
			var p4 = p3 + (ax3 * (p3 - p2).Length);
			segs.Add((p1, p2));
			segs.Add((p2, p3));
			segs.Add((p3, p4));
		}

		var sumMinDist = 0.0;
		var count = 0;
		for (var v = 0; v < pts.Count; v++)
		{
			var isFinger = false;
			foreach (var inf in geom.Vertices[v].Influences())
			{
				if (inf.Weight >= 0.25f && resolved.TryGetValue(inf.BoneIndex, out var hd2) && fingerBoneIds.Contains(hd2))
				{
					isFinger = true;
					break;
				}
			}
			if (!isFinger)
				continue;

			var best = double.MaxValue;
			foreach (var (a, b) in segs)
				best = Math.Min(best, PointSegmentDist(pts[v], a, b));
			sumMinDist += best;
			count++;
		}

		return count > 0 ? sumMinDist / count : 0.0;
	}

	private static int CountHandVertices(SourceGeometry geom, IReadOnlyDictionary<int, int> resolved, HashSet<int> handSet)
	{
		var cnt = 0;
		for (var v = 0; v < geom.Vertices.Count; v++)
		{
			foreach (var inf in geom.Vertices[v].Influences())
			{
				if (inf.Weight >= 0.20f && resolved.TryGetValue(inf.BoneIndex, out var hd2) && handSet.Contains(hd2))
				{
					cnt++;
					break;
				}
			}
		}
		return cnt;
	}

	private static double PointSegmentDist(Vec3 p, Vec3 a, Vec3 b)
	{
		var ab = b - a;
		var lenSq = Vec3.Dot(ab, ab);
		if (lenSq <= 1e-12)
			return (p - a).Length;
		var t = RetargetMath.Clamp(Vec3.Dot(p - a, ab) / lenSq, 0.0, 1.0);
		return (p - (a + (ab * t))).Length;
	}

	private static List<int> Subtree(List<int>?[] children, int root)
	{
		var list = new List<int>();
		var stack = new Stack<int>();
		stack.Push(root);
		while (stack.Count > 0)
		{
			var cur = stack.Pop();
			list.Add(cur);
			var ch = children[cur];
			if (ch is null)
				continue;
			foreach (var c in ch)
				stack.Push(c);
		}
		return list;
	}

	private static double Smoothstep(double x)
	{
		var t = RetargetMath.Clamp(x, 0.0, 1.0);
		return t * t * (3.0 - (2.0 * t));
	}

	private static Mat4 AroundPoint(Vec3 pivot, Mat3 rot) =>
		RetargetMath.Trans(pivot) * rot.ToMat4() * RetargetMath.Trans(pivot * -1.0);

	private static Mat4 MakeWorld(Mat3 rot, Vec3 origin) =>
		new(
			rot.M11, rot.M12, rot.M13, origin.X,
			rot.M21, rot.M22, rot.M23, origin.Y,
			rot.M31, rot.M32, rot.M33, origin.Z,
			0.0, 0.0, 0.0, 1.0);
}
