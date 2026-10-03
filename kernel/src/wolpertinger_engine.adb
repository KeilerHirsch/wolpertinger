with Interfaces;
with Wolpertinger_Bounded_Text;

package body Wolpertinger_Engine with SPARK_Mode is

   package Text renames Wolpertinger_Bounded_Text;

   use type Interfaces.Unsigned_64;
   use type Types.Galaxy_Realm;

   function Make_Jump_Fact
     (State : State_Types.Kernel_State) return Facts.Jump_Fact is
   begin
      return
        (Cursor              => State.Last_Cursor,
         System_Address      => State.Location.System_Address,
         Star_System         => State.Location.Star_System,
         Position            => State.Location.Position,
         Jump_Distance       => State.Last_Jump_Distance,
         Fuel_Used           => State.Fuel.Used,
         Fuel_Level          => State.Fuel.Level,
         Location_Provenance => State.Location.Provenance,
         Location_Freshness  => State.Location.Freshness,
         Fuel_Provenance     => State.Fuel.Provenance,
         Fuel_Freshness      => State.Fuel.Freshness);
   end Make_Jump_Fact;

   function Make_Commander_Vessel_Fact
     (State : State_Types.Kernel_State) return Facts.Commander_Vessel_Fact is
   begin
      return
        (Cursor     => State.Last_Cursor,
         Data       => State.Commander_Vessel.Data,
         Provenance => State.Commander_Vessel.Provenance,
         Freshness  => State.Commander_Vessel.Freshness);
   end Make_Commander_Vessel_Fact;

   procedure Apply
     (State       : in out State_Types.Kernel_State;
      Observation : Types.Observation;
      Result      : out Apply_Result)
   is
      Candidate : State_Types.Kernel_State := State;
   begin
      Result := (others => <>);
      if State.Has_Last_Cursor
        and then State_Types.Same_Cursor (State.Last_Cursor, Observation.Cursor)
      then
         Result.Status :=
           (if State_Types.Same_Digest (State.Last_Digest, Observation.Evidence_Digest)
            then Idempotent
            else Integrity_Fault);
         return;
      end if;

      if not State_Types.Is_Next_Cursor (State, Observation) then
         Result.Status := Sequence_Gap;
         return;
      end if;

      case Observation.Kind is
         when Types.Session_Bound =>
            if Observation.Profile.FID.Length = 0
              or else Observation.Profile.Realm = Types.Unknown
              or else
                (Observation.Provenance /= Types.Local_Journal
                 and then Observation.Provenance /= Types.Sample)
            then
               Result.Status := Invalid_Observation;
               return;
            end if;
            if State.Bound
              and then
                (State.Profile.Save_Epoch = Interfaces.Unsigned_64'Last
                 or else
                   Observation.Profile.Save_Epoch
                     /= State.Profile.Save_Epoch + 1)
            then
               Result.Status := Identity_Conflict;
               return;
            end if;

            Candidate.Bound := True;
            Candidate.Profile := Observation.Profile;
            Candidate.Session_Id := Observation.Session_Id;
            if State.Bound then
               Candidate.Location := (others => <>);
               Candidate.Fuel := (others => <>);
               Candidate.Last_Jump_Distance := (others => <>);
               Candidate.Commander_Vessel := (others => <>);
            end if;

         when Types.FSD_Jump =>
            if not State_Types.Identity_Matches (State, Observation) then
               Result.Status := Identity_Conflict;
               return;
            end if;
            if Observation.Provenance /= Types.Local_Journal
              and then Observation.Provenance /= Types.Sample
            then
               Result.Status := Invalid_Observation;
               return;
            end if;
            Candidate.Location.Known := True;
            Candidate.Location.System_Address := Observation.Jump.System_Address;
            Candidate.Location.Star_System := Observation.Jump.Star_System;
            Candidate.Location.Position := Observation.Jump.Position;
            Candidate.Location.Provenance := Observation.Provenance;
            Candidate.Location.Freshness := State_Types.Current;

            Candidate.Fuel.Known := True;
            Candidate.Fuel.Level := Observation.Jump.Fuel_Level;
            Candidate.Fuel.Used := Observation.Jump.Fuel_Used;
            Candidate.Fuel.Provenance := Observation.Provenance;
            Candidate.Fuel.Freshness := State_Types.Current;
            Candidate.Last_Jump_Distance := Observation.Jump.Jump_Distance;

         when Types.Commander_Vessel =>
            if not State_Types.Identity_Matches (State, Observation) then
               Result.Status := Identity_Conflict;
               return;
            end if;
            if Observation.Provenance /= Types.Frontier_API
              and then Observation.Provenance /= Types.Sample
            then
               Result.Status := Invalid_Observation;
               return;
            end if;
            if Observation.Commander_Vessel.Commander_Name_Value.Length
                 not in 1 .. Types.Commander_Name_Max_UTF8_Bytes
              or else Observation.Commander_Vessel.Vessel_Name_Value.Length
                 not in 1 .. Types.Vessel_Name_Max_UTF8_Bytes
              or else not Text.Is_Valid_UTF8
                (Types.Text_128 (Observation.Commander_Vessel.Commander_Name_Value))
              or else not Text.Is_Valid_UTF8
                (Types.Text_128 (Observation.Commander_Vessel.Vessel_Name_Value))
            then
               Result.Status := Invalid_Observation;
               return;
            end if;
            Candidate.Commander_Vessel.Known := True;
            Candidate.Commander_Vessel.Data := Observation.Commander_Vessel;
            Candidate.Commander_Vessel.Provenance := Observation.Provenance;
            Candidate.Commander_Vessel.Freshness := State_Types.Current;
      end case;

      Candidate.Has_Last_Cursor := True;
      Candidate.Last_Cursor := Observation.Cursor;
      Candidate.Last_Digest := Observation.Evidence_Digest;
      Candidate.Last_Message_Count := Observation.Message_Count;

      State := Candidate;
      Result.Status := Applied;
      if Observation.Kind = Types.FSD_Jump then
         Result.Has_Jump_Fact := True;
         Result.Jump := Make_Jump_Fact (State);
      elsif Observation.Kind = Types.Commander_Vessel then
         Result.Has_Commander_Vessel_Fact := True;
         Result.Commander_Vessel := Make_Commander_Vessel_Fact (State);
      end if;
   end Apply;

end Wolpertinger_Engine;
