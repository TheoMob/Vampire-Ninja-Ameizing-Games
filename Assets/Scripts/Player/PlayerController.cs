using System;
using System.Collections;
using IPlayerState;
using UnityEngine;
using static IPlayerState.PlayerStateController;
using MovementStatsController;

namespace TarodevController
{
    /// <summary>
    /// Mob/Theo here, The base of this script was build on the TaroDev controller script, be sure to thank him and include in the credits when the game launches https://discord.gg/tarodev
    /// </summary>
    [RequireComponent(typeof(Rigidbody2D), typeof(Collider2D))]
    public class PlayerController : MonoBehaviour, IPlayerController
    {
        [SerializeField] private ScriptableStats _stats;
        private PlayerState currentState;
        private PlayerAnimationsHandler _animHandler;

        private AudioManager _audioManager;

        private PlayerStateController _stateController;
        private Rigidbody2D _rb;
        private CapsuleCollider2D _col;
        private SpriteRenderer _spr;

        public FrameInput _frameInput;
        public Vector2 _frameVelocity;

        private bool _cachedQueryStartInColliders;

        #region Interface

        public Vector2 FrameInput => _frameInput.Move;
        public event Action<bool, float> GroundedChanged;
        public event Action Jumped;

        #endregion
        private float _time;

        private void Awake()
        {
          _stateController = GetComponent<PlayerStateController>();
          _animHandler = GetComponent<PlayerAnimationsHandler>();
          
          _audioManager = GameObject.Find("AudioManager").GetComponent<AudioManager>();

          _rb = GetComponent<Rigidbody2D>();
          _col = GetComponent<CapsuleCollider2D>();
          _spr = GetComponent<SpriteRenderer>();

          _cachedQueryStartInColliders = Physics2D.queriesStartInColliders;
          _gravityScale = _stats.FallAcceleration;

          dashState = DashState.Ready;
        }
        private void Update()
        {
          _time += Time.deltaTime;
          currentState = _stateController.GetCurrentState();

          if (currentState == PlayerState.Defeated || currentState == PlayerState.CantMove)
            return;

          GatherInput();
          SlowHandler();
        }

        private void FixedUpdate()
        {
          if (currentState == PlayerState.CantMove)
          {
            _rb.velocity = Vector2.zero;
            return;
          }

          CheckCollisions();
          HandleGravity();
          HandleCrouch();
          HandleJump();
          HandleDirection();
          WallSlide();
          WallJump();
          HandleDash();
          ApplyMovement();
        }
        private void GatherInput()
        {
            _frameInput = new FrameInput
            {
                JumpDown = Input.GetButtonDown("Jump"),
                JumpHeld = Input.GetButton("Jump"),
                DashPressed = Input.GetButtonDown("Fire3") || Input.GetKeyDown(KeyCode.K),
                Move = new Vector2(Input.GetAxisRaw("Horizontal"), Input.GetAxisRaw("Vertical")),
            };

            if (_stats.SnapInput)
            {
                _frameInput.Move.x = Mathf.Abs(_frameInput.Move.x) < _stats.HorizontalDeadZoneThreshold ? 0 : Mathf.Sign(_frameInput.Move.x);
                _frameInput.Move.y = Mathf.Abs(_frameInput.Move.y) < _stats.VerticalDeadZoneThreshold ? 0 : Mathf.Sign(_frameInput.Move.y);
            }

            if (_frameInput.JumpDown)
            {
                _jumpToConsume = true;
                _timeJumpWasPressed = _time;
            }

            if (_frameInput.DashPressed)
            {
              _timeDashWasPressed = _time;
              _dashPressed = true;
            }
        }

        #region Collisions

        public bool _grounded;
        private float _frameLeftGrounded = float.MinValue;
        private void CheckCollisions()
        {
            if (currentState == PlayerState.Defeated)
              return;
      
            Physics2D.queriesStartInColliders = false;

            // Ground and Ceiling
            bool groundHit = Physics2D.CapsuleCast(_col.bounds.center, _col.size, _col.direction, 0, Vector2.down, _stats.GrounderDistance, LayerMask.GetMask("Ground") | LayerMask.GetMask("OneWayPlatform"));
            bool ceilingHit = Physics2D.CapsuleCast(_col.bounds.center, _col.size, _col.direction, 0, Vector2.up, _stats.GrounderDistance, LayerMask.GetMask("Ground"));

            // Hit a Ceiling
            if (ceilingHit)
              _frameVelocity.y = Mathf.Min(0, _frameVelocity.y);  

            // Landed on the Ground
            if (!_grounded && groundHit)
            {
                _dashReset = true;
                _grounded = true;
                _coyoteUsable = true;
                _bufferedJumpUsable = true;
                _endedJumpEarly = false;
                GroundedChanged?.Invoke(true, Mathf.Abs(_frameVelocity.y));

               _dashPressed = false; // so it doesnt dash after landing if the dash was pressed in the air

               jumpState = JumpState.Idle;
            }
            // Left the Ground
            else if (_grounded && !groundHit)
            {
                _grounded = false;
                _frameLeftGrounded = _time;
                GroundedChanged?.Invoke(false, 0);
            }

            Physics2D.queriesStartInColliders = _cachedQueryStartInColliders;
        }

        #endregion


        #region Jumping

        public JumpState jumpState = JumpState.Idle;
        public bool _jumpToConsume;
        private bool _bufferedJumpUsable;
        private bool _endedJumpEarly;
        private bool _coyoteUsable;
        private float _timeJumpWasPressed;

        private bool HasBufferedJump => _bufferedJumpUsable && _time < _timeJumpWasPressed + _stats.JumpBuffer;
        private bool CanUseCoyote => _coyoteUsable && !_grounded && _time < _frameLeftGrounded + _stats.CoyoteTime && currentState != PlayerState.Crouching;
        private void HandleJump()
        {
          if (currentState == PlayerState.Dashing)
            return;

          TestJumpState();

          _endedJumpEarly = ShouldEndJumpEarly();

          if (_frameInput.Move.y < 0)
          {
            _endedJumpEarly = false;
            _timeJumpWasPressed = 0;
            _bufferedJumpUsable = false;
            _coyoteUsable = false;
            _jumpToConsume = false;
            return;
          }

          if (!_jumpToConsume && !HasBufferedJump)
            return;

          if (_grounded || CanUseCoyote) ExecuteJump();

          if (currentState == PlayerState.WallSliding || wallJumpBufferTimer > 0f)
            return;

          _jumpToConsume = false;
        }

        private bool ShouldEndJumpEarly()
        {
          bool improperState = currentState == PlayerState.WallSliding || currentState == PlayerState.WallJumping ? true : false;

          return !_endedJumpEarly && !_grounded && !_frameInput.JumpHeld && _rb.velocity.y > 0
              && !improperState && !improperState && wallJumpBufferTimer < 0f;
        }

        private void ExecuteJump()
        {
            _endedJumpEarly = false;
            _timeJumpWasPressed = 0;
            _bufferedJumpUsable = false;
            _coyoteUsable = false;
            _frameVelocity.y = _stats.JumpPower;
            Jumped?.Invoke();

            jumpState = JumpState.Ascending;

            _audioManager.Play("Jump");
        }
        private void TestJumpState()
        {
          bool endJump = _rb.velocity.y <= 0 || _endedJumpEarly;

          if (jumpState == JumpState.Ascending && endJump)
          {
            StartCoroutine(HandleJumpTransition());
          }
        }
        private IEnumerator HandleJumpTransition()
        {
          jumpState = JumpState.Transition;    
          yield return new WaitForSeconds(_stats.JumpTransitionDuration);
          jumpState = JumpState.Idle;  
        }

        #endregion

        #region Crouch
        private void HandleCrouch()
        {
          if (currentState == PlayerState.Dashing || currentState == PlayerState.Defeated)
            return;

          if (!_grounded || _frameVelocity.y >= 0)
            return;

          if(currentState == PlayerState.Crouching)
            _frameVelocity.x = 0;
        }

        #endregion
        #region Horizontal

        private void HandleDirection()
        {
          if (currentState == PlayerState.Dashing || currentState == PlayerState.Crouching)
            return;

          if (_frameInput.Move.x == 0)
          {
            var deceleration = _grounded ? _stats.GroundDeceleration : _stats.AirDeceleration;

            _frameVelocity.x = Mathf.MoveTowards(_frameVelocity.x, 0, deceleration * Time.fixedDeltaTime);
          }
          else
          {
            _frameVelocity.x = Mathf.MoveTowards(_frameVelocity.x, _frameInput.Move.x * _stats.MaxSpeed, _stats.Acceleration * Time.fixedDeltaTime);
          }
        }

        #endregion

        #region Gravity

        private float _gravityScale;

        private void HandleGravity()
        {
          if (currentState == PlayerState.Dashing)
            return;

          if (currentState == PlayerState.Defeated)
          {
            _frameVelocity.y = Mathf.MoveTowards(_frameVelocity.y, -_stats.MaxFallSpeed, _stats.FallAcceleration * Time.fixedDeltaTime);
            return;
          }

          if (_grounded && _frameVelocity.y <= 0f) // means it's grounded
          {
            _frameVelocity.y = _stats.GroundingForce;
            return;
          }

          var fallSpeed = _gravityScale;
          
          if (_endedJumpEarly || _frameVelocity.y < 0)
          {
            fallSpeed *= _stats.JumpEndEarlyGravityModifier;
          }

          if (jumpState == JumpState.Transition && currentState != PlayerState.WallJumping)
          {
            _frameVelocity.y = 0;
            return;
          }

          _frameVelocity.y = Mathf.MoveTowards(_frameVelocity.y, -_stats.MaxFallSpeed, fallSpeed * Time.fixedDeltaTime);
        }

        #endregion

        #region Dash

        public DashState dashState;
        private bool _groundHitAfterDash = false; // not used for now
        public bool _dashReset = false;
        private bool _dashPressed = false;
        private float _timeDashWasPressed;

        GameManager _gameManager;

        [HideInInspector] public bool isDashVertical; // just PlayerStateController should access it
        [SerializeField] private float slowdownFactor = 0.05f;
        [SerializeField] private float slowdownTransitionLength = .5f;

        private bool doSlow = false;
        private void SlowHandler()
        {
          if (Time.timeScale == 1f && !doSlow)
            return;
          
          if (doSlow)
          {
            Time.timeScale = slowdownFactor;
            Time.fixedDeltaTime = Time.timeScale * .02f;
            doSlow = false; 
          }
          else
          {
            Time.timeScale += 1f/ slowdownTransitionLength * Time.unscaledDeltaTime;
            Time.timeScale = Math.Clamp(Time.timeScale, 0f, 1f);
          }
        }

        private void OnTriggerEnter2D(Collider2D col)
        {
          if (currentState != PlayerState.Dashing)
            return;

          if (col.CompareTag("Enemy"))
          {
            _dashReset = true;
            doSlow = true;
          }

          if (col.CompareTag("BounceBack"))
          {
            _dashReset = true;
            dashState = DashState.Ready;
            _gravityScale = _stats.FallAcceleration;
            CancelInvoke("DashCooldownHandler");
            CancelInvoke("DashDurationHandler");
            CancelInvoke("DashDelayedExecution");
            CancelInvoke("DelayToReturnGravity");

            float direction = -_frameInput.Move.x;
            if (direction == 0)
              direction = _spr.flipX ? 1 : -1;

            _frameVelocity = new Vector2(direction * 30, 20);
            return;
          }
        }

        private void OnTriggerStay2D(Collider2D col) 
        {
          if (currentState != PlayerState.Dashing || _dashReset)
            return;
          
          if (col.CompareTag("Enemy"))
          {
            _dashReset = true;
            doSlow = true;
          }
        }

        private void HandleDash()
        {
          if (currentState == PlayerState.WallSliding || currentState == PlayerState.WallJumping || currentState == PlayerState.Crouching || currentState == PlayerState.Defeated)
          {
            _dashPressed = false;
            return;
          }

          switch(dashState)
          {
            case DashState.Ready:
              if (_dashPressed)
              {
                ExecuteDash();
              }
            break;

            case DashState.Dashing:
            if (_dashPressed && _dashReset)
              ExecuteDash();
            else
              _dashPressed = false;
            break;

            case DashState.Cooldown:
             if (_dashPressed && _dashReset)
               ExecuteDash();
            break;
          }
        }

        private void ExecuteDash()
        {
          _dashPressed = false;
          _dashReset = false;
          jumpState = JumpState.Idle;
          _animHandler.SendDashAnimation();

          CancelInvoke("DashCooldownHandler");
          CancelInvoke("DashDurationHandler");
          CancelInvoke("DashDelayedExecution");
          CancelInvoke("DelayToReturnGravity");
          Invoke("DashCooldownHandler", _stats.DashCooldown);
          Invoke("DashDurationHandler", _stats.DashDuration);
          Invoke("DashDelayedExecution", _stats.DashInputDelay * Time.timeScale);
        }

        private void DashCooldownHandler()
        {
          if (dashState == DashState.Cooldown && _grounded)
            dashState = DashState.Ready;
        }

        private void DashDurationHandler()
        {
          if (dashState == DashState.Dashing)
          {
            dashState = DashState.Cooldown;

            if (_frameVelocity.y > 0) // after the dash ends, the player should stop going up, the lack of gravity makes it float a little
              _frameVelocity.y = _frameVelocity.y / 2;

            Invoke("DelayToReturnGravity", _stats.DashGravityReturnDelay * Time.timeScale);
          }
        }

        private void DelayToReturnGravity()
        {
          _gravityScale = _stats.FallAcceleration;
        }

        private void DashDelayedExecution()
        {
          _gravityScale = 0;
          dashState = DashState.Dashing;

          switch(_frameInput.Move)
          {
            case var _ when _frameInput.Move == Vector2.zero: // horizontal dashes without pressing any directions
              float direction = _spr.flipX ? -1 : 1;
              _frameVelocity.x = direction * _stats.DashSpeed;
              _frameVelocity.y = 0;
              isDashVertical = false;
            break;

            case var _ when _frameInput.Move.x != 0 && _frameInput.Move.y == 0: // horizontal dashes pressing left or right
              _frameVelocity.x = _frameInput.Move.x * _stats.DashSpeed;
              _frameVelocity.y = 0;
              isDashVertical = false;
            break;

            case var _ when _frameInput.Move.x == 0 && _frameInput.Move.y != 0: // vertical dashes
              _frameVelocity.x = 0;
              _frameVelocity.y = _frameInput.Move.y * _stats.DashVerticalSpeed;
              isDashVertical = true;
            break;

            case var _ when _frameInput.Move.x != 0 && _frameInput.Move.y != 0: // diagonal dashes
              _frameVelocity.x = _frameInput.Move.x * _stats.DashSpeed;
              _frameVelocity.y = _frameInput.Move.y * _stats.DashVerticalSpeed * 0.5f;
              isDashVertical = false;
            break;
          }
        }

        #endregion

        #region wallJump
        private float wallJumpDirection;
        private float wallJumpBufferTimer;

        public bool wallJumping = false;

        private void WallSlide()
        {
          if (currentState == PlayerState.Dashing || currentState == PlayerState.Crouching)
            return;

          if (currentState == PlayerState.WallSliding)
          {
            _frameVelocity.y = Mathf.Clamp(_frameVelocity.y, -_stats.WallSlidingSpeed, float.MaxValue);
            jumpState = JumpState.Idle;
          }
        }

        private void WallJump()
        {
          if (currentState == PlayerState.Dashing || currentState == PlayerState.Crouching)
            return;

          if (_grounded)
          {
            wallJumpBufferTimer = 0f;
            return;
          }

          if (currentState == PlayerState.WallSliding)
          {
            wallJumpDirection = _spr.flipX ? 1 : -1;
            wallJumpBufferTimer = _stats.WallJumpBuffer;

            CancelInvoke(nameof(StopWallJumping));
          }
          else
            wallJumpBufferTimer -= Time.deltaTime;


          if (_jumpToConsume && wallJumpBufferTimer > 0f)
          {
            wallJumping = true;
            _stateController.SetState(PlayerState.WallJumping);
            
            _frameVelocity = new Vector2(wallJumpDirection * _stats.WallJumpPower.x, _stats.WallJumpPower.y);
            wallJumpBufferTimer = 0f;
            
            _audioManager.Play("Jump");
          }

          Invoke(nameof(StopWallJumping), _stats.WallJumpDuration);
        }

        private void StopWallJumping()
        {
          wallJumping = false;
        }
        #endregion

        // #region Auxiliary Jump
        // public bool auxiliaryJump = false;
        // private void AuxiliaryJump()
        // {
        //   if (_jumpToConsume && wallJumpBufferTimer > 0f)
        //   {
        //     wallJumping = true;
        //     _stateController.SetState(PlayerState.WallJumping);
            
        //     _frameVelocity = new Vector2(wallJumpDirection * _stats.WallJumpPower.x, _stats.WallJumpPower.y);
        //     wallJumpBufferTimer = 0f;
        //   }

        //   Invoke(nameof(StopAuxiliaryJump), _stats.WallJumpDuration);
        // }

        // private void StopAuxiliaryJump()
        // {
        //   auxiliaryJump = false;
        // }
        // #endregion

        private void ApplyMovement() => _rb.velocity = _frameVelocity;

        // private void ApplyMovement()
        // {
        //   Debug.Log(_frameVelocity);
        //   _rb.velocity = _frameVelocity;
        // }

#if UNITY_EDITOR
        private void OnValidate()
        {
            if (_stats == null) Debug.LogWarning("Please assign a ScriptableStats asset to the Player Controller's Stats slot", this);
        }
#endif
    }

    public struct FrameInput
    {
      public bool JumpDown;
      public bool JumpHeld;
      public bool DashPressed;
      public Vector2 Move;
    }

    public interface IPlayerController
    {
      public event Action<bool, float> GroundedChanged;
      public event Action Jumped;
      public Vector2 FrameInput { get; }
    }

    public enum JumpState
    {
      Idle,
      Ascending,
      Transition,
    }

    public enum DashState
    {
      Ready,
      Dashing,
      Cooldown
    }
}