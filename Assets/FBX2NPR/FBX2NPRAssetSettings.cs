using System;
using UnityEngine;

namespace FBX2NPR
{
    /// <summary>
    /// 모델 하나에 대한 툰 변환 설정입니다.
    ///
    /// 이 값은 .meta 의 userData 에 JSON 으로 들어갑니다. 별도 에셋을 만들지 않는 이유는
    /// 설정이 모델을 따라다녀야 하기 때문입니다. 파일을 옮기거나 남에게 넘겨도 같이 갑니다.
    /// </summary>
    [Serializable]
    public class FBX2NPRAssetSettings
    {
        /// <summary>꺼져 있으면 임포터가 아무것도 하지 않습니다. 기본은 꺼짐입니다.</summary>
        public bool enabled = false;

        /// <summary>
        /// 변환 결과의 룩. 0 = 서브컬처 툰(얼굴 평탄화 + 이목구비 그리기 + 두 단 명암 + 먹선), 1 = 회화풍(붓질 알베도 + 표준 Lit 부드러운 음영 + 얇은 실루엣 잉크).
        /// 파싱·기준점·비교 렌더는 두 모드가 공유하고, 텍스처 처리와 재질·외곽선 설정만 갈라집니다. 회화풍은 메시 노멀을 건드리지 않습니다.
        /// </summary>
        [Range(0, 1)] public int style = 0;

        /// <summary>회화풍 모드의 실루엣 잉크 라인 세기 (0 = 없음). 프리팹의 PainterlyRig 슬라이더 초기값.</summary>
        [Range(0f, 1f)] public float painterlyInk = 1f;

        /// <summary>회화풍 모드에서 얼굴 피부(파서의 피부·코·목·귀 라벨)를 얼마나 부드럽게 할지. 1 = 붓털 줄무늬 끄고 패치 경계를 녹임, 0 = 피부도 옷처럼 붓질.
        /// 사진 피부(Ch07)는 1 이어야 주름 같은 선이 안 남고, 손그림 얼굴(The Boss)은 0.5 쯤이 붓터치를 더 남긴다.</summary>
        [Range(0f, 1f)] public float painterlySkinSmooth = 1f;

        /// <summary>회화풍 모드에서 눈가(눈·눈썹 라벨 둘레)의 원본 '어두운 선'(아이라인 날개·속눈썹·눈썹 진한 선)을 매끈한 피부 위에 Darken(min)으로 얹는 세기.
        /// 마스크로 오려 붙이지 않고 피부보다 충분히 어두운 화소만 덧그리므로 경계 계단이 없습니다. 0 = 안 함.</summary>
        [Range(0f, 1f)] public float painterlyEyeInk = 1f;

        /// <summary>회화풍 모드에서 피부 위에 그려진 작은 장식(반창고·문신·스티커처럼 피부와 색조가 다른 덩어리)을 붓질·스무딩 뒤에 원본으로 되살리는 세기. 0 = 안 함.
        /// 피부색이 진하거나 옅어진 것(코·귀·그늘·하이라이트·수염)은 대상이 아니다. The Boss 의 뺨 반창고가 지워져서 넣었다 (기록 97번).</summary>
        [Range(0f, 1f)] public float painterlyDecalKeep = 1f;

        /// <summary>회화풍 모드에서 붓질을 하지 않고 원본 그대로 또렷하게 둘 메시 (이름에 포함된 문자열, 세미콜론 구분). 안구·속눈썹 카드.
        /// 이름이 없어도 얼굴에 붙은 작은 덩어리(머리 본에 묶인 메시의 작은 연결 성분: 안구·이·입 안)는 자동으로 제외합니다.</summary>
        public string painterlySharpContains = "eye;iris;pupil;cornea;lash;eyeline";

        public bool IsPainterlySharp(string meshName)
        {
            string mn = (meshName ?? "").ToLowerInvariant();
            foreach (var s in Split(painterlySharpContains)) if (s.Length > 0 && mn.Contains(s)) return true;
            return false;
        }

        /// <summary>방사형 정렬 강도. 0 이면 원본, 1 이면 완전 구형.</summary>
        [Range(0f, 1f)] public float mix = 0.4f;

        /// <summary>
        /// 노멀 맵을 얼마나 남길지. 정점 노멀만 평탄화하고 맵을 그대로 두면 소용이 없습니다.
        /// 실측: 맵을 25% 만 켜도 밴드 덩어리 수가 mix 와 무관하게 230 근처로 고정됐습니다.
        /// </summary>
        [Range(0f, 1f)] public float normalMapStrength = 0f;

        /// <summary>대상 본 이름에 포함될 문자열. 믹사모는 mixamorig8:Head 같은 식입니다.</summary>
        public string headBoneContains = "head";

        /// <summary>이 가중치 미만인 정점은 건드리지 않습니다.</summary>
        [Range(0f, 1f)] public float weightThreshold = 0.5f;

        /// <summary>
        /// 경계를 부드럽게 넘길지 여부. 끄면 임계값에서 딱 잘리고,
        /// 그러면 목에 가로 단절선이 생깁니다 (초기 블렌더 실험에서 실제로 발생).
        /// </summary>
        public bool smoothFalloff = true;

        /// <summary>
        /// 얼굴로 볼 메시의 재질명에 포함될 문자열. 세미콜론 구분.
        /// 머리 본 가중치만으로는 머리카락·속눈썹·모자·이·시가까지 전부 잡힙니다 (3체 실험에서 실측).
        /// 재질이 없는 메시는 이름 제외 목록만 적용합니다.
        /// </summary>
        public string faceMaterialContains = "body;skin;face";

        /// <summary>이 문자열이 메시명에 있으면 제외합니다. 재질이 skin 이어도 이(teeth)는 얼굴이 아닙니다.</summary>
        public string excludeMeshContains = "hair;eyelash;lash;teeth;tooth;cigar;hat;brow;beard";

        /// <summary>
        /// FBX 안의 텍스처 링크가 끊긴 에셋용. "재질명=텍스처명;..." 형식.
        /// 유니티짱 FBX 는 마야 절대경로(D:/Dropbox/...)만 들고 있어 유니티가 텍스처를 못 찾는다 (임포트 검사에서 실측).
        /// </summary>
        public string albedoByMaterial = "";

        /// <summary>
        /// 측정·변환에서 아예 제외할 메시. 유니티짱의 볼 홍조(cheek)처럼 얼굴 위에 떠 있는 반투명 데칼 판은
        /// 노멀 정리 대상도 아니고, 컷아웃 셰이더로는 불투명 판으로 그려져 마스크와 성분 수를 망친다 (4체 실험에서 실측).
        /// </summary>
        public string ignoreMeshContains = "";

        /// <summary>
        /// 변환 도구가 얼굴 텍스처에서 구운 빛을 빼고 피부를 평평하게 구울지. 기본은 켬.
        /// 피부가 한 색이 아닌 캐릭터(비늘·무늬가 특징인 Jones 같은 경우)는 무늬가 사라지므로 끕니다.
        /// </summary>
        public bool flattenFaceSkin = true;

        /// <summary>
        /// 변환 도구는 칠할 평탄색이 원래 얼굴 평균색과 색이 많이 다르면 '피부가 한 색이 아니다'로 보고 평탄화를 건너뜁니다.
        /// 그 검사를 무시하고 억지로 평탄화하려면 켭니다.
        /// </summary>
        public bool flattenIgnoreColorCheck = false;

        /// <summary>
        /// 얼굴 밖 텍스처를 어디까지 평탄화할지. 얼굴만 평평하면 목부터 아래가 사진·채색 질감 그대로라 머리가 떠 보입니다.
        /// 0 = 얼굴만, 1 = 피부까지(목·손·팔을 얼굴과 같은 색으로), 2 = 옷·모자까지(색 무리마다 평평한 색면, 또렷한 선만 남김).
        /// 얼굴 평탄화를 건너뛴 캐릭터(이미 평평하거나 피부가 한 색이 아님)는 이 값과 상관없이 몸도 건드리지 않습니다.
        /// </summary>
        [Range(0, 2)] public int bodyFlatten = 1;

        /// <summary>
        /// 얼굴 파싱 모델(Assets/FBX2NPR/Models/resnet18.onnx)로 '어디를 평탄화하고 어디를 남길지'를 정합니다. 기본은 켬.
        /// 모델이 없거나 파싱이 실패하면 예전 방식(머리 본 가중치 + 색 통계)으로 돌아갑니다. 손그림·비인간 얼굴은 파서가 눈·눈썹을 못 잡을 수 있고,
        /// 그때는 피부 라벨 안에서 예전 대비 규칙을 폴백으로 씁니다.
        /// </summary>
        public bool useFaceParser = true;

        /// <summary>
        /// 파서가 눈·눈썹을 찾은 얼굴에서 이목구비를 사진 조각으로 남기지 않고 라벨 모양대로 그립니다(눈썹 단색 면, 눈 위 먹선, 콧구멍 타원, 입술 템플릿 + 입선). 기본은 켬.
        /// 끄면 라벨이 정한 자리의 원본 조각을 남깁니다(사진 조각과 평평한 피부가 섞여 '때가 낀' 느낌 — 사용자 지적).
        /// </summary>
        public bool drawFeatures = true;

        /// <summary>몸 평탄화에서 손대지 않을 메시 (이름에 포함된 문자열, 세미콜론 구분). 눈·이·속눈썹·머리카락 카드.</summary>
        public string bodyFlattenSkipContains = "eye;teeth;tooth;tongue;lash;brow;hair";

        /// <summary>
        /// 이름에 이 문자열이 든 메시는 몸 평탄화에서 피부로 보지 않습니다 (세미콜론 구분).
        /// 피부 판정은 재질 규칙 + 색 + 밝기로 하는데, 살구색·베이지색 옷은 색과 밝기로는 피부와 가를 수 없습니다. 이름이 한 겹 더 막습니다.
        /// </summary>
        public string clothMeshContains = "pants;shirt;suit;jacket;coat;dress;skirt;shoe;boot;heel;hat;cap;glove;sock;belt;tie;cloth;armor;vest;hood;scarf;uwagi";

        public bool IsClothMesh(string meshName)
        {
            string mn = (meshName ?? "").ToLowerInvariant();
            foreach (var s in Split(clothMeshContains)) if (s.Length > 0 && mn.Contains(s)) return true;
            return false;
        }

        public bool IsBodyFlattenSkipped(string meshName)
        {
            string mn = (meshName ?? "").ToLowerInvariant();
            foreach (var s in Split(bodyFlattenSkipContains)) if (s.Length > 0 && mn.Contains(s)) return true;
            return false;
        }

        /// <summary>
        /// 툰 모드에서 눈매 먹선(아이라인)을 그렸을 때 숨길 메시 (이름에 포함된 문자열, 세미콜론 구분). 사진 속눈썹 카드는 알파 컷아웃 가닥이 눈 둘레에 점점이 남아 그린 아이라인과 겹친다.
        /// 비우면 숨기지 않는다. 회화풍 모드는 아래 painterlyHideMeshContains 를 쓴다.
        /// </summary>
        public string toonHideMeshContains = "eyelash;lash";

        /// <summary>
        /// 회화풍 모드에서 눈매 잉크(원본 아이라인을 붓질 피부에 덧그림)를 얹었을 때 숨길 메시 (이름에 포함된 문자열, 세미콜론 구분). 비우면 숨기지 않는다.
        /// Ch07 회화풍에서 아래 눈꺼풀 밑에 1픽셀 검은 점이 프레임마다 자리를 바꾸며 깜빡였는데, 속눈썹 카드를 숨기니 사라졌다(72프레임 83~107개 → 0, 기록 98번). 툰 모드와 같은 처리.
        /// </summary>
        public string painterlyHideMeshContains = "eyelash;lash";

        public bool IsPainterlyHiddenMesh(string meshName)
        {
            string mn = (meshName ?? "").ToLowerInvariant();
            foreach (var s in Split(painterlyHideMeshContains)) if (s.Length > 0 && mn.Contains(s)) return true;
            return false;
        }

        /// <summary>
        /// 툰 모드 입술 손보정: 아랫입술의 아래 끝을 한쪽에서만 입꼬리 쪽으로 갈수록 더 내린다 (기준 길이 배수). + 는 텍스처 x 가 큰 쪽, − 는 작은 쪽, 0 = 안 함.
        /// UV 가 좌우 비대칭이라 텍스처는 대칭인데 렌더에서 한쪽 입꼬리 아래가 비는 모델용 (Ch07 0.6). 자동으로 찾지 못해 모델별로 적는다.
        /// </summary>
        public float toonLipBottomExtend = 0f;

        /// <summary>툰 모드 입술 손보정: 한쪽 입꼬리 끝만 바깥으로 더 늘인다 (기준 길이 배수, + 는 텍스처 x 가 큰 쪽). 메시의 입 주름 선이 채움보다 바깥까지 뻗는 모델용 (Ch07 +0.2). 나머지 텍셀은 그대로.</summary>
        public float toonLipTipExtend = 0f;

        /// <summary>
        /// 툰 모드 입술 손보정(렌더 공간 대칭): 정면 렌더에서 한쪽 아랫입술을 거울로 비춘 영역 중 비어 있는 반대쪽 픽셀이 쓰는 텍셀만 입술색으로 칠한다.
        /// +1 = 화면 오른쪽 아랫입술을 왼쪽에 맞춤, -1 = 반대, 0 = 안 함. UV 가 좌우 비대칭이라 텍스처는 대칭인데 렌더에서 한쪽 날개가 비는 모델용 (Ch07 +1). 윗입술·반대쪽은 그대로.
        /// </summary>
        public int toonLipSymmetrySide = 0;

        public bool IsToonHiddenMesh(string meshName)
        {
            string mn = (meshName ?? "").ToLowerInvariant();
            foreach (var s in Split(toonHideMeshContains)) if (s.Length > 0 && mn.Contains(s)) return true;
            return false;
        }

        public bool IsIgnoredMesh(string meshName)
        {
            string mn = (meshName ?? "").ToLowerInvariant();
            foreach (var ig in Split(ignoreMeshContains)) if (ig.Length > 0 && mn.Contains(ig)) return true;
            return false;
        }

        /// <summary>재질명에 대응하는 텍스처 이름. 표에 없으면 null.</summary>
        public string AlbedoNameFor(string matName)
        {
            if (string.IsNullOrEmpty(albedoByMaterial) || string.IsNullOrEmpty(matName)) return null;
            string mn = matName.Trim().ToLowerInvariant();
            foreach (var pair in albedoByMaterial.Split(';'))
            {
                var kv = pair.Split('='); if (kv.Length != 2) continue;
                if (kv[0].Trim().ToLowerInvariant() == mn) return kv[1].Trim();
            }
            return null;
        }

        /// <summary>얼굴 메시인지 판정합니다. 포스트프로세서와 실험 코드가 같은 규칙을 씁니다.</summary>
        public bool IsFaceMesh(string meshName, Material[] mats)
        {
            string mn = (meshName ?? "").ToLowerInvariant();
            foreach (var ex in Split(excludeMeshContains))
                if (ex.Length > 0 && mn.Contains(ex)) return false;

            var inc = Split(faceMaterialContains);
            if (inc.Length == 0) return true;

            bool anyMat = false;
            if (mats != null)
            {
                foreach (var m in mats)
                {
                    if (m == null) continue;
                    anyMat = true;
                    string n = m.name.ToLowerInvariant();
                    foreach (var k in inc) if (k.Length > 0 && n.Contains(k)) return true;
                }
            }
            // 재질 정보가 아직 없으면 (임포트 순서) 이름 제외만으로 판정합니다.
            return !anyMat;
        }

        static string[] Split(string s)
        {
            if (string.IsNullOrEmpty(s)) return new string[0];
            var a = s.ToLowerInvariant().Split(';');
            for (int i = 0; i < a.Length; i++) a[i] = a[i].Trim();
            return a;
        }

        public static FBX2NPRAssetSettings FromJson(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            try
            {
                var s = JsonUtility.FromJson<FBX2NPRAssetSettings>(json);
                // userData 에 다른 툴이 쓴 JSON 이 들어 있을 수 있으므로 형태를 확인합니다.
                return (s != null && s.headBoneContains != null) ? s : null;
            }
            catch { return null; }
        }

        public string ToJson() { return JsonUtility.ToJson(this); }
    }
}