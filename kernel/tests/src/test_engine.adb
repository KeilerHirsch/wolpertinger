with AUnit.Assertions;
with Interfaces;
with Wolpertinger_Bounded_Text;
with Wolpertinger_Engine;
with Wolpertinger_State;
with Wolpertinger_Types;

procedure Test_Engine is
   package Assert renames AUnit.Assertions;
   package Text renames Wolpertinger_Bounded_Text;
   package Engine renames Wolpertinger_Engine;
   package State_Types renames Wolpertinger_State;
   package Types renames Wolpertinger_Types;

   use type Interfaces.Integer_64;
   use type Interfaces.Unsigned_64;
   use type Engine.Apply_Status;
   use type State_Types.Freshness_State;
   use type State_Types.Kernel_State;
   use type Types.Byte_16;
   use type Types.Decimal_64;
   use type Types.Observation_Cursor;
   use type Types.Profile_Key;
   use type Types.Source_Provenance;
   use type Types.Commander_Vessel_Data;

   Session_Id : constant Types.Byte_16 := [others => 16#A0#];
   Profile : constant Types.Profile_Key :=
     (FID        => Text.To_Text_64 ("FTEST0001"),
      Realm      => Types.Live,
      Save_Epoch => 0);
   function Session_Observation return Types.Observation is
   begin
      return
        (Cursor              => (Evidence_Sequence => 1, Message_Ordinal => 0),
         Evidence_Digest     => [others => 16#11#],
         Session_Id          => Session_Id,
         Profile             => Profile,
         Kind                => Types.Session_Bound,
         Source_Time_Present => True,
         Source_Time_Unix_Ms => 1_700_000_000_000,
         Observed_Unix_Ms    => 1_700_000_000_100,
         Commit_Unix_Ms      => 1_700_000_000_200,
         Message_Count       => 1,
         Provenance          => Types.Local_Journal,
         Jump                => (others => <>),
         Protocol_Version    => 1,
         Commander_Vessel    => (others => <>));
   end Session_Observation;

   function Jump_Observation return Types.Observation is
   begin
      return
        (Cursor              => (Evidence_Sequence => 2, Message_Ordinal => 0),
         Evidence_Digest     => [others => 16#22#],
         Session_Id          => Session_Id,
         Profile             => Profile,
         Kind                => Types.FSD_Jump,
         Source_Time_Present => True,
         Source_Time_Unix_Ms => 1_700_000_001_000,
         Observed_Unix_Ms    => 1_700_000_001_100,
         Commit_Unix_Ms      => 1_700_000_001_200,
         Message_Count       => 1,
         Provenance          => Types.Local_Journal,
         Jump                =>
           (Star_System    => Text.To_Text_128 ("W. Grantler NX-42"),
            System_Address => 1_234_567_890_123_456_789,
            Position       =>
              (X => (Coefficient => 12_345, Exponent => -3),
               Y => (Coefficient => -6_789, Exponent => -2),
               Z => (Coefficient => 42, Exponent => 0)),
            Jump_Distance  => (Coefficient => 55_359, Exponent => -3),
            Fuel_Used      => (Coefficient => 4_843_642, Exponent => -6),
            Fuel_Level     => (Coefficient => 27_123, Exponent => -3)),
         Protocol_Version    => 1,
         Commander_Vessel    => (others => <>));
   end Jump_Observation;

   function Repeat_Byte (Value : Character; Length : Positive) return String is
      Result : String (1 .. Length) := [others => Value];
   begin
      return Result;
   end Repeat_Byte;

   function Multibyte_128 return String is
      Result : String (1 .. 128) := (others => 'x');
   begin
      for I in 0 .. 41 loop
         Result (I * 2 + 1) := Character'Val (16#C3#);
         Result (I * 2 + 2) := Character'Val (16#A9#);
      end loop;
      return Result;
   end Multibyte_128;

   function Commander_Vessel_Observation
     (Commander, Vessel : String;
      Provenance : Types.Source_Provenance := Types.Sample)
      return Types.Observation
   is
      Result : Types.Observation := Session_Observation;
   begin
      Result.Protocol_Version := 2;
      Result.Cursor := (Evidence_Sequence => 2, Message_Ordinal => 0);
      Result.Evidence_Digest := [others => 16#33#];
      Result.Kind := Types.Commander_Vessel;
      Result.Provenance := Provenance;
      Result.Commander_Vessel :=
        (Commander_Name_Value =>
           Types.Commander_Name (Text.To_Text_128 (Commander)),
         Commander_Alive => True,
         Commander_Docked => False,
         Commander_On_Foot => True,
         Vessel_Name_Value => Types.Vessel_Name (Text.To_Text_128 (Vessel)),
         Ship_Alive => True);
      return Result;
   end Commander_Vessel_Observation;

   procedure Assert_Commander_Vessel
     (Commander, Vessel : String;
      Expected : Engine.Apply_Status;
      Label : String;
      Provenance : Types.Source_Provenance := Types.Sample) is
      State : State_Types.Kernel_State;
      Before : State_Types.Kernel_State;
      Result : Engine.Apply_Result;
      Session : constant Types.Observation := Session_Observation;
      Observation : constant Types.Observation :=
        Commander_Vessel_Observation (Commander, Vessel, Provenance);
   begin
      Engine.Apply (State, Session, Result);
      Assert.Assert (Result.Status = Engine.Applied, Label & " session binding");
      Before := State;
      Engine.Apply (State, Observation, Result);
      Assert.Assert (Result.Status = Expected, Label & " status");
      if Expected = Engine.Applied then
         Assert.Assert (State.Commander_Vessel.Known, Label & " trusted state");
         Assert.Assert (Result.Has_Commander_Vessel_Fact, Label & " typed fact");
         Assert.Assert
           (State.Commander_Vessel.Data = Observation.Commander_Vessel,
            Label & " trusted value");
      else
         Assert.Assert (State = Before, Label & " rejection must not mutate state");
         Assert.Assert
           (not Result.Has_Commander_Vessel_Fact,
            Label & " rejection must not emit a trusted fact");
      end if;
   end Assert_Commander_Vessel;

   procedure Test_Rebind_And_Jump_Provenance is
      State : State_Types.Kernel_State;
      Before : State_Types.Kernel_State;
      Result : Engine.Apply_Result;
      Session : Types.Observation := Session_Observation;
      Jump : Types.Observation := Jump_Observation;
      Rebind : Types.Observation := Session_Observation;
      Bad_Rebind : Types.Observation := Session_Observation;
      Unauthorized : Types.Observation := Jump_Observation;
   begin
      Engine.Apply (State, Session, Result);
      Assert.Assert (Result.Status = Engine.Applied, "initial session must bind");
      Engine.Apply (State, Jump, Result);
      Assert.Assert (Result.Status = Engine.Applied, "initial jump must apply");
      Assert.Assert (State.Location.Known, "initial jump must establish location");

      Rebind.Cursor := (Evidence_Sequence => 3, Message_Ordinal => 0);
      Rebind.Evidence_Digest := [others => 16#44#];
      Rebind.Session_Id := [others => 16#B0#];
      Rebind.Profile.Save_Epoch := 1;
      Engine.Apply (State, Rebind, Result);
      Assert.Assert (Result.Status = Engine.Applied, "next save epoch must rebind");
      Assert.Assert (State.Profile.Save_Epoch = 1, "rebind must advance save epoch");
      Assert.Assert (State.Session_Id = Rebind.Session_Id, "rebind must replace session id");
      Assert.Assert (not State.Location.Known, "rebind must clear session location");
      Assert.Assert (not State.Fuel.Known, "rebind must clear session fuel");
      Assert.Assert (not State.Commander_Vessel.Known, "rebind must clear profile snapshot");

      Before := State;
      Bad_Rebind := Rebind;
      Bad_Rebind.Cursor := (Evidence_Sequence => 4, Message_Ordinal => 0);
      Bad_Rebind.Evidence_Digest := [others => 16#55#];
      Bad_Rebind.Session_Id := [others => 16#C0#];
      Bad_Rebind.Profile.Save_Epoch := 1;
      Engine.Apply (State, Bad_Rebind, Result);
      Assert.Assert
        (Result.Status = Engine.Identity_Conflict,
         "same save epoch must not rebind twice");
      Assert.Assert (State = Before, "invalid rebind must not mutate state");

      Unauthorized.Cursor := (Evidence_Sequence => 4, Message_Ordinal => 0);
      Unauthorized.Evidence_Digest := [others => 16#66#];
      Unauthorized.Session_Id := Rebind.Session_Id;
      Unauthorized.Profile := Rebind.Profile;
      Unauthorized.Provenance := Types.Community;
      Engine.Apply (State, Unauthorized, Result);
      Assert.Assert
        (Result.Status = Engine.Invalid_Observation,
         "community jump provenance must be rejected");
      Assert.Assert (State = Before, "unauthorized provenance must not mutate state");
   end Test_Rebind_And_Jump_Provenance;

   procedure Test_Commander_Vessel_Bounds is
      Malformed : constant String := [1 => Character'Val (16#C3#)];
      Overlong : constant String :=
        [1 => Character'Val (16#C0#), 2 => Character'Val (16#80#)];
      Surrogate : constant String :=
        [1 => Character'Val (16#ED#),
         2 => Character'Val (16#A0#),
         3 => Character'Val (16#80#)];
      Above_Unicode_Max : constant String :=
        [1 => Character'Val (16#F4#),
         2 => Character'Val (16#90#),
         3 => Character'Val (16#80#),
         4 => Character'Val (16#80#)];
      Isolated_Continuation : constant String :=
        [1 => Character'Val (16#80#)];
      Empty : constant String := "";
      Max_128 : constant String := Repeat_Byte ('A', 128);
      Multi_128 : constant String := Multibyte_128;
   begin
      Assert_Commander_Vessel ("A", "B", Engine.Applied, "one-byte names");
      Assert_Commander_Vessel (Max_128, "V", Engine.Applied, "128-byte commander");
      Assert_Commander_Vessel ("C", Max_128, Engine.Applied, "128-byte vessel");
      Assert_Commander_Vessel
        (Multi_128, "V", Engine.Applied, "multibyte commander at 128 bytes");
      Assert_Commander_Vessel
        ("C", Multi_128, Engine.Applied, "multibyte vessel at 128 bytes");
      Assert_Commander_Vessel (Empty, "V", Engine.Invalid_Observation, "empty commander");
      Assert_Commander_Vessel ("C", Empty, Engine.Invalid_Observation, "empty vessel");
      Assert_Commander_Vessel
        (Malformed, "V", Engine.Invalid_Observation, "malformed commander UTF-8");
      Assert_Commander_Vessel
        ("C", Malformed, Engine.Invalid_Observation, "malformed vessel UTF-8");
      Assert_Commander_Vessel
        (Overlong, "V", Engine.Invalid_Observation, "overlong commander UTF-8");
      Assert_Commander_Vessel
        ("C", Overlong, Engine.Invalid_Observation, "overlong vessel UTF-8");
      Assert_Commander_Vessel
        (Surrogate, "V", Engine.Invalid_Observation, "surrogate commander UTF-8");
      Assert_Commander_Vessel
        ("C", Surrogate, Engine.Invalid_Observation, "surrogate vessel UTF-8");
      Assert_Commander_Vessel
        (Above_Unicode_Max, "V", Engine.Invalid_Observation, "out-of-range commander UTF-8");
      Assert_Commander_Vessel
        ("C", Above_Unicode_Max, Engine.Invalid_Observation, "out-of-range vessel UTF-8");
      Assert_Commander_Vessel
        (Isolated_Continuation, "V", Engine.Invalid_Observation,
         "continuation-byte commander UTF-8");
      Assert_Commander_Vessel
        ("C", Isolated_Continuation, Engine.Invalid_Observation,
         "continuation-byte vessel UTF-8");
      Assert_Commander_Vessel
        ("C", "V", Engine.Invalid_Observation, "unauthorized provenance",
         Types.Local_Journal);
   end Test_Commander_Vessel_Bounds;

   procedure Test_Happy_Path is
      State : State_Types.Kernel_State;
      Result : Engine.Apply_Result;
      Session : constant Types.Observation := Session_Observation;
      Jump : constant Types.Observation := Jump_Observation;
   begin
      Engine.Apply (State, Session, Result);
      Assert.Assert (Result.Status = Engine.Applied, "session must bind");
      Assert.Assert (State.Bound, "state must be bound");
      Assert.Assert (State.Profile = Profile, "profile must match");
      Assert.Assert (State.Session_Id = Session_Id, "session id must match");
      Engine.Apply (State, Jump, Result);
      Assert.Assert (Result.Status = Engine.Applied, "jump must apply");
      Assert.Assert
        (State.Location.System_Address = Jump.Jump.System_Address,
         "jump must update location");
      Assert.Assert
        (State.Location.Provenance = Types.Local_Journal,
         "location provenance must remain explicit");
      Assert.Assert
        (State.Location.Freshness = State_Types.Current,
         "fresh journal location must be current");
      Assert.Assert
        (State.Fuel.Level = Jump.Jump.Fuel_Level,
         "fuel level must update");
      Assert.Assert
        (State.Last_Cursor = Jump.Cursor,
         "last cursor must advance to jump");
      Assert.Assert (Result.Has_Jump_Fact, "applied jump must emit a fact");
      Assert.Assert
        (Result.Jump.System_Address = State.Location.System_Address,
         "jump fact must reflect authoritative location");
      Assert.Assert
        (Result.Jump.Location_Freshness = State.Location.Freshness,
         "fact freshness must come from authoritative state");
   end Test_Happy_Path;

   procedure Test_Ordering_And_Identity is
      State : State_Types.Kernel_State;
      Before : State_Types.Kernel_State;
      Result : Engine.Apply_Result;
      Session : Types.Observation := Session_Observation;
      Jump : Types.Observation := Jump_Observation;
   begin
      Jump.Cursor := (Evidence_Sequence => 1, Message_Ordinal => 0);
      Before := State;
      Engine.Apply (State, Jump, Result);
      Assert.Assert
        (Result.Status = Engine.Identity_Conflict,
         "jump before binding must fail identity");
      Assert.Assert (State = Before, "identity rejection must not mutate state");

      Engine.Apply (State, Session, Result);
      Assert.Assert (Result.Status = Engine.Applied, "session must bind once");
      Before := State;
      Engine.Apply (State, Session, Result);
      Assert.Assert (Result.Status = Engine.Idempotent, "same cursor/digest must be idempotent");
      Assert.Assert (State = Before, "idempotent resend must not mutate state");

      Session.Evidence_Digest (1) := 16#99#;
      Engine.Apply (State, Session, Result);
      Assert.Assert
        (Result.Status = Engine.Integrity_Fault,
         "same cursor/different digest must fault");
      Assert.Assert (State = Before, "integrity fault must not mutate state");

      Jump := Jump_Observation;
      Jump.Cursor := (Evidence_Sequence => 3, Message_Ordinal => 0);
      Engine.Apply (State, Jump, Result);
      Assert.Assert (Result.Status = Engine.Sequence_Gap, "future cursor must gap");
      Assert.Assert (State = Before, "sequence gap must not mutate state");
      Jump := Jump_Observation;
      Jump.Profile.FID := Text.To_Text_64 ("FOTHER999");
      Engine.Apply (State, Jump, Result);
      Assert.Assert
        (Result.Status = Engine.Identity_Conflict,
         "profile mismatch must fail closed");
      Assert.Assert (State = Before, "identity conflict must not mutate state");
   end Test_Ordering_And_Identity;

begin
   Test_Happy_Path;
   Test_Ordering_And_Identity;
   Test_Rebind_And_Jump_Provenance;
   Test_Commander_Vessel_Bounds;
end Test_Engine;
