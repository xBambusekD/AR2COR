﻿using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using ROSBridgeLib.geometry_msgs;
using ROSBridgeLib.std_msgs;
using ROSBridgeLib.tf2_msgs;

public class TransformPublisher : MonoBehaviour {

    public string frame_id;
    public string child_frame_id;
    public GameObject parentGameObject;

    private TFMessageMsg tfMsg;
    private System.DateTime epochStart;

    // Use this for initialization
    void Start () {
        epochStart = new System.DateTime(1970, 1, 1, 0, 0, 0, System.DateTimeKind.Utc);
    }
	
	// Update is called once per frame
	void Update () {
        if(SystemStarter.Instance.calibrated) {
            //Vector3 relativePositionToParent = gameObject.transform.InverseTransformPoint(parentGameObject.transform.position);
            Vector3 relativePositionToParent = parentGameObject.transform.InverseTransformPoint(gameObject.transform.position);
            Quaternion relativeRotationToParent = Quaternion.Inverse(parentGameObject.transform.rotation) * gameObject.transform.rotation;

            
            //double seconds = (System.DateTime.UtcNow - epochStart).TotalSeconds;
            //Debug.Log(seconds);
            //var values = seconds.ToString().Split('.');
            //int secs = int.Parse(values[0]);
            //int nsecs = int.Parse(values[1]);

            tfMsg = new TFMessageMsg(new List<TransformStampedMsg>() {new TransformStampedMsg(new HeaderMsg(0, new TimeMsg(0, 0), frame_id), child_frame_id,
                new TransformMsg(new Vector3Msg(relativePositionToParent.x, -relativePositionToParent.y, relativePositionToParent.z), 
                    new QuaternionMsg(-relativeRotationToParent.x, relativeRotationToParent.y, -relativeRotationToParent.z, relativeRotationToParent.w)))});

            ROSCommunicationManager.Instance.ros.Publish(TFPublisher.GetMessageTopic(), tfMsg);
        }
	}
}
