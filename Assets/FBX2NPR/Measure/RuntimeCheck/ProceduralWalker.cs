using UnityEngine;

namespace FBX2NPR.RuntimeCheck
{
    /// <summary>
    /// 애니메이션 클립이 없는 테스트 세트용 걷기 흉내. Update 에서 본을 직접 돌린다 (Animator 가 본을 움직이는 것과 같은 단계 — 리그는 LateUpdate).
    /// 다리·팔은 반대 위상으로 흔들고, 엉덩이는 위아래로, 고개는 좌우로 돌리고 끄덕인다. 본 이름은 'LeftUpLeg' 처럼 포함 문자열로 찾는다(믹사모·유니티짱 공통).
    /// 프레임 수로 진행(fixedStep)해 에디터 배치·플레이어 어디서든 같은 프레임에 같은 자세가 나온다. 실제 클립(믹사모 걷기 등)이 있으면 Animator 를 쓰면 된다.
    /// </summary>
    public class ProceduralWalker : MonoBehaviour
    {
        public float speed = 1f;            // 0 이면 정지(정지 프레임 비교용)
        public float stride = 1.6f;         // 걸음 주기(Hz)
        public float fixedStep = 1f / 60f;  // 프레임당 진행 시간. 0 이면 Time.deltaTime
        public float legSwing = 32f, armSwing = 28f, headYaw = 25f, headNod = 8f, bob = 0.02f, spineTwist = 6f;
        public float phase;                 // 캐릭터마다 다르게 주면 동시에 같은 자세가 안 나온다

        Transform lLeg, rLeg, lArm, rArm, head, spine, hips;
        Quaternion lLeg0, rLeg0, lArm0, rArm0, head0, spine0, hips0; Vector3 hipsPos0;
        float t;

        void Awake()
        {
            foreach (var tr in GetComponentsInChildren<Transform>(true))
            {
                string n = tr.name.ToLowerInvariant();
                if (tr.GetComponent<Renderer>() != null) continue;
                if (lLeg == null && n.Contains("leftupleg")) lLeg = tr;
                else if (rLeg == null && n.Contains("rightupleg")) rLeg = tr;
                else if (lArm == null && n.EndsWith("leftarm")) lArm = tr;
                else if (rArm == null && n.EndsWith("rightarm")) rArm = tr;
                else if (head == null && n.EndsWith("head")) head = tr;
                else if (spine == null && (n.EndsWith("spine") || n.EndsWith("spine1"))) spine = tr;
                else if (hips == null && n.EndsWith("hips")) hips = tr;
            }
            if (lLeg) lLeg0 = lLeg.localRotation; if (rLeg) rLeg0 = rLeg.localRotation; if (lArm) lArm0 = lArm.localRotation; if (rArm) rArm0 = rArm.localRotation;
            if (head) head0 = head.localRotation; if (spine) spine0 = spine.localRotation; if (hips) { hips0 = hips.localRotation; hipsPos0 = hips.localPosition; }
        }

        public string Found()
        {
            return "다리 " + (lLeg && rLeg ? "O" : "X") + " 팔 " + (lArm && rArm ? "O" : "X") + " 머리 " + (head ? "O" : "X") + " 척추 " + (spine ? "O" : "X") + " 엉덩이 " + (hips ? "O" : "X");
        }

        void Update()
        {
            t += (fixedStep > 0f ? fixedStep : Time.deltaTime) * speed;
            float w = t * stride * Mathf.PI * 2f + phase;
            float s = Mathf.Sin(w), c = Mathf.Cos(w);
            Vector3 right = transform.right, up = transform.up;
            // 초기 자세로 되돌린 뒤 월드 축으로 돌린다 (본의 로컬 축이 리그마다 달라서)
            if (lLeg) { lLeg.localRotation = lLeg0; lLeg.rotation = Quaternion.AngleAxis(legSwing * s, right) * lLeg.rotation; }
            if (rLeg) { rLeg.localRotation = rLeg0; rLeg.rotation = Quaternion.AngleAxis(-legSwing * s, right) * rLeg.rotation; }
            if (lArm) { lArm.localRotation = lArm0; lArm.rotation = Quaternion.AngleAxis(-armSwing * s, right) * Quaternion.AngleAxis(60f, transform.forward) * lArm.rotation; }
            if (rArm) { rArm.localRotation = rArm0; rArm.rotation = Quaternion.AngleAxis(armSwing * s, right) * Quaternion.AngleAxis(-60f, transform.forward) * rArm.rotation; }
            if (spine) { spine.localRotation = spine0; spine.rotation = Quaternion.AngleAxis(spineTwist * s, up) * spine.rotation; }
            if (head)
            {
                head.localRotation = head0;
                head.rotation = Quaternion.AngleAxis(headYaw * Mathf.Sin(w * 0.5f), up) * Quaternion.AngleAxis(headNod * Mathf.Sin(w * 2f), right) * head.rotation;
            }
            if (hips) { hips.localRotation = hips0; hips.localPosition = hipsPos0 + Vector3.up * (bob * Mathf.Abs(c)) / Mathf.Max(1e-4f, hips.lossyScale.y) * transform.localScale.y; }
        }
    }
}
