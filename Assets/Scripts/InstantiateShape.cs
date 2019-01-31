﻿using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using HoloToolkit.Unity.InputModule;

public class InstantiateShape : MonoBehaviour {

    public GameObject Shape;
    private GameObject worldAnchor;

    void Start() {
        worldAnchor = GameObject.FindGameObjectWithTag("world_anchor");
    }

    public void InstantiateShapePrefab() {
        CollisionEnvironmentManager.Instance.CreateNewPrimitive(Shape, worldAnchor.transform.InverseTransformPoint(GazeManager.Instance.GazeOrigin + GazeManager.Instance.GazeNormal * 1.5f));
    }
}
